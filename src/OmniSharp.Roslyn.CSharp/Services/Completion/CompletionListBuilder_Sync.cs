﻿#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Models;
using OmniSharp.Models.v1.Completion;
using OmniSharp.Roslyn.CSharp.Helpers;
using OmniSharp.Roslyn.CSharp.Services.Intellisense;
using OmniSharp.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CompletionItem = OmniSharp.Models.v1.Completion.CompletionItem;
using CompletionTriggerKind = OmniSharp.Models.v1.Completion.CompletionTriggerKind;
using CSharpCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using CSharpCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;

namespace OmniSharp.Roslyn.CSharp.Services.Completion
{
    internal static partial class CompletionListBuilder
    {
        internal static async Task<(IReadOnlyList<CompletionItem>, bool)> BuildCompletionItemsSync(
            Document document,
            SourceText sourceText,
            int position,
            CSharpCompletionService completionService,
            CSharpCompletionList completions,
            TextSpan typedSpan,
            bool expectingImportedItems,
            bool isSuggestionMode)
        {
            var completionsBuilder = new List<CompletionItem>(completions.Items.Length);
            var seenUnimportedCompletions = false;
            var commitCharacterRuleCache = new Dictionary<ImmutableArray<CharacterSetModificationRule>, IReadOnlyList<char>>();
            var commitCharacterRuleBuilder = new HashSet<char>();

            var completionTasksAndProviderNames = completions.Items.SelectAsArray((document, completionService), (completion, arg) =>
            {
                var providerName = completion.GetProviderName();
                if (providerName is CompletionItemExtensions.TypeImportCompletionProvider or
                                    CompletionItemExtensions.ExtensionMethodImportCompletionProvider)
                {
                    return (null, providerName);
                }
                else
                {
                    return ((Task<CompletionChange>?)arg.completionService.GetChangeAsync(arg.document, completion), providerName);
                }
            });
            for (int i = 0; i < completions.Items.Length; i++)
            {
                TextSpan changeSpan = typedSpan;
                var completion = completions.Items[i];
                var insertTextFormat = InsertTextFormat.PlainText;
                string labelText = completion.DisplayTextPrefix + completion.DisplayText + completion.DisplayTextSuffix;
                List<LinePositionSpanTextChange>? additionalTextEdits = null;
                string? insertText = null;
                string? filterText = null;
                string? sortText = null;
                var (changeTask, providerName) = completionTasksAndProviderNames[i];
                switch (providerName)
                {
                    case CompletionItemExtensions.TypeImportCompletionProvider or CompletionItemExtensions.ExtensionMethodImportCompletionProvider:
                        changeSpan = typedSpan;
                        insertText = completion.DisplayText;
                        seenUnimportedCompletions = true;
                        sortText = '1' + completion.SortText;
                        filterText = null;
                        break;

                    case CompletionItemExtensions.InternalsVisibleToCompletionProvider:
                        // if the completion is for the hidden Misc files project, skip it
                        if (completion.DisplayText == Configuration.OmniSharpMiscProjectName) continue;
                        goto default;

                    default:
                        {
                            // Except for import completion, we just resolve the change up front in the sync version. It's only expensive
                            // for override completion, but there's not a heck of a lot we can do about that for the sync scenario
                            Debug.Assert(changeTask is not null);
                            var change = await changeTask!;

                            // Roslyn will give us the position to move the cursor after the completion is entered.
                            // However, this is in the _new_ document, after changes have been applied. In order to
                            // snippetize the insertion text, we need to calculate the offset as we move along the
                            // edits, subtracting or adding the difference for edits that do not insersect the current
                            // span.
                            var adjustedNewPosition = change!.NewPosition;

                            // There must be at least one change that affects the current location, or something is seriously wrong
                            Debug.Assert(change.TextChanges.Any(change => change.Span.IntersectsWith(position)));

                            foreach (var textChange in change.TextChanges)
                            {
                                if (!textChange.Span.IntersectsWith(position))
                                {
                                    additionalTextEdits ??= new();
                                    additionalTextEdits.Add(GetChangeForTextAndSpan(textChange.NewText!, textChange.Span, sourceText));

                                    if (adjustedNewPosition is int newPosition)
                                    {
                                        // Find the diff between the original text length and the new text length.
                                        var diff = (textChange.NewText?.Length ?? 0) - textChange.Span.Length;

                                        // If the new text is longer than the replaced text, we want to subtract that
                                        // length from the current new position to find the adjusted position in the old
                                        // document. If the new text was shorter, diff will be negative, and subtracting
                                        // will result in increasing the adjusted position as expected
                                        adjustedNewPosition = newPosition - diff;
                                    }
                                }
                                else
                                {
                                    // Either there should be no new position, or it should be within the text that is being added
                                    // by this change.
                                    Debug.Assert(adjustedNewPosition is null ||
                                        (adjustedNewPosition.Value <= textChange.Span.Start + textChange.NewText!.Length) &&
                                        (adjustedNewPosition.Value >= textChange.Span.Start));

                                    changeSpan = textChange.Span;
                                    (insertText, insertTextFormat) = getPossiblySnippitizedInsertText(textChange, adjustedNewPosition);

                                    // If we're expecting there to be unimported types, put in an explicit sort text to put things already in scope first.
                                    // Otherwise, omit the sort text if it's the same as the label to save on space.
                                    sortText = expectingImportedItems
                                        ? '0' + completion.SortText
                                        : labelText == completion.SortText ? null : completion.SortText;

                                    // If the completion is replacing a bigger range than the previously-typed word, we need to have the filter
                                    // text compensate. Clients will use the range of the text edit to determine the thing that is being filtered
                                    // against. For example, override completion:
                                    //
                                    //    override $$
                                    //    |--------| Range that is being changed by the completion
                                    //
                                    // That means vscode will consider "override <additional user input>" when looking to see whether the item
                                    // still matches. To compensate, we add the start of the replacing range, up to the start of the current word,
                                    // to ensure the item isn't silently filtered out.

                                    if (changeSpan != typedSpan)
                                    {
                                        if (typedSpan.Start < changeSpan.Start)
                                        {
                                            // This means that some part of the currently-typed text is an exact match for the start of the
                                            // change, so chop off changeSpan.Start - typedSpan.Start from the filter text to get it to match
                                            // up with the range
                                            int prefixMatchElement = changeSpan.Start - typedSpan.Start;
                                            Debug.Assert(completion.FilterText.StartsWith(sourceText.GetSubText(new TextSpan(typedSpan.Start, prefixMatchElement)).ToString()));
                                            filterText = completion.FilterText.Substring(prefixMatchElement);
                                        }
                                        else
                                        {
                                            var prefix = sourceText.GetSubText(TextSpan.FromBounds(changeSpan.Start, typedSpan.Start)).ToString();
                                            filterText = prefix + completion.FilterText;
                                        }
                                    }
                                    else
                                    {
                                        filterText = labelText == completion.FilterText ? null : completion.FilterText;
                                    }
                                }
                            }

                            break;
                        }
                }

                var commitCharacters = buildCommitCharacters(completion.Rules.CommitCharacterRules, isSuggestionMode, commitCharacterRuleCache, commitCharacterRuleBuilder);

                completionsBuilder.Add(new CompletionItem
                {
                    Label = labelText,
                    TextEdit = GetChangeForTextAndSpan(insertText!, changeSpan, sourceText),
                    InsertTextFormat = insertTextFormat,
                    AdditionalTextEdits = additionalTextEdits,
                    SortText = sortText,
                    FilterText = filterText,
                    Kind = getCompletionItemKind(completion.Tags),
                    Detail = completion.InlineDescription,
                    Data = i,
                    Preselect = completion.Rules.SelectionBehavior == CompletionItemSelectionBehavior.HardSelection,
                    CommitCharacters = commitCharacters,
                });
            }

            return (completionsBuilder, seenUnimportedCompletions);

            static (string?, InsertTextFormat) getPossiblySnippitizedInsertText(TextChange change, int? adjustedNewPosition)
            {
                if (adjustedNewPosition is not int newPosition || change.NewText is null || newPosition == change.Span.Start + change.NewText.Length)
                {
                    return (change.NewText, InsertTextFormat.PlainText);
                }

                // Roslyn wants to move the cursor somewhere inside the result. Substring from the
                // requested start to the new position, and from the new position to the end of the
                // string.
                int midpoint = newPosition - change.Span.Start;
                var beforeText = LspSnippetHelpers.Escape(change.NewText.Substring(0, midpoint));
                var afterText = LspSnippetHelpers.Escape(change.NewText.Substring(midpoint));

                return ($"{beforeText}$0{afterText}", InsertTextFormat.Snippet);
            }

            static CompletionItemKind getCompletionItemKind(ImmutableArray<string> tags)
            {
                foreach (var tag in tags)
                {
                    if (s_roslynTagToCompletionItemKind.TryGetValue(tag, out var itemKind))
                    {
                        return itemKind;
                    }
                }

                return CompletionItemKind.Text;
            }

            static IReadOnlyList<char>? buildCommitCharacters(
                ImmutableArray<CharacterSetModificationRule> characterRules,
                bool isSuggestionMode,
                Dictionary<ImmutableArray<CharacterSetModificationRule>, IReadOnlyList<char>> commitCharacterRulesCache,
                HashSet<char> commitCharactersBuilder)
            {
                if (characterRules.IsEmpty)
                {
                    // Use defaults
                    return isSuggestionMode ? DefaultRulesWithoutSpace : CompletionRules.Default.DefaultCommitCharacters;
                }

                if (commitCharacterRulesCache.TryGetValue(characterRules, out var cachedRules))
                {
                    return cachedRules;
                }

                addAllCharacters(CompletionRules.Default.DefaultCommitCharacters);

                foreach (var modifiedRule in characterRules)
                {
                    switch (modifiedRule.Kind)
                    {
                        case CharacterSetModificationKind.Add:
                            commitCharactersBuilder.UnionWith(modifiedRule.Characters);
                            break;

                        case CharacterSetModificationKind.Remove:
                            commitCharactersBuilder.ExceptWith(modifiedRule.Characters);
                            break;

                        case CharacterSetModificationKind.Replace:
                            commitCharactersBuilder.Clear();
                            addAllCharacters(modifiedRule.Characters);
                            break;
                    }
                }

                // VS has a more complex concept of a commit mode vs suggestion mode for intellisense.
                // LSP doesn't have this, so mock it as best we can by removing space ` ` from the list
                // of commit characters if we're in suggestion mode.
                if (isSuggestionMode)
                {
                    commitCharactersBuilder.Remove(' ');
                }

                var finalCharacters = commitCharactersBuilder.ToList();
                commitCharactersBuilder.Clear();

                commitCharacterRulesCache.Add(characterRules, finalCharacters);

                return finalCharacters;

                void addAllCharacters(ImmutableArray<char> characters)
                {
                    foreach (var @char in characters)
                    {
                        commitCharactersBuilder.Add(@char);
                    }
                }
            }
        }
    }
}
