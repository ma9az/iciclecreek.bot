﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Iciclecreek.Bot.Builder.Dialogs.Recognizers.Lupa.PatternMatchers.Matchers;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;

namespace Iciclecreek.Bot.Builder.Dialogs.Recognizers.Lupa.PatternMatchers
{
    public abstract class PatternMatcher
    {
        /// <summary>
        /// See if matcher is true or not
        /// </summary>
        /// <param name="matchContext">match context.</param>
        /// <param name="start">start index</param>
        /// <returns>-1 if not match, else new start index</returns>
        public abstract MatchResult Matches(MatchContext matchContext, int start);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="pattern">pattern to parse</param>
        /// <param name="exactAnalyzer">exact analyzer to use</param>
        /// <param name="fuzzyAnalyzer">fuzzy analyzer to use</param>
        /// <param name="fuzzyMatch">if true changes default for text token to fuzzyMatch</param>
        /// <returns></returns>
        public static PatternMatcher Parse(string pattern, Analyzer exactAnalyzer, Analyzer fuzzyAnalyzer, bool fuzzyMatch = false)
        {
            SequencePatternMatcher sequence = new SequencePatternMatcher();

            bool inVariations = false;
            bool inModifiers = false;
            bool modifierFuzzyMatch = false;
            Ordinality modifierOrdinality = Ordinality.One;
            List<string> variations = new List<string>();
            StringBuilder sb = new StringBuilder();
            foreach (char ch in pattern)
            {
                if (!inVariations)
                {
                    switch (ch)
                    {
                        case '(':
                            if (sb.Length > 0)
                            {
                                if (fuzzyMatch)
                                {
                                    sequence.PatternMatchers.Add(CreateFuzzyTextPatternMatcher(sb.ToString(), fuzzyAnalyzer));
                                }
                                else
                                {
                                    sequence.PatternMatchers.Add(CreateTextPatternMatcher(sb.ToString(), exactAnalyzer));
                                }
                                sb.Clear();
                            }

                            inVariations = true;
                            inModifiers = false;
                            modifierOrdinality = Ordinality.One;
                            modifierFuzzyMatch = false;
                            variations.Clear();
                            break;

                        default:
                            sb.Append(ch);
                            break;
                    }
                }
                else
                {
                    if (inModifiers == false)
                    {
                        switch (ch)
                        {
                            case '|':
                                variations.Add(sb.ToString());
                                sb.Clear();
                                break;

                            case ')':
                                if (sb.Length > 0)
                                {
                                    variations.Add(sb.ToString());
                                }
                                sb.Clear();
                                inModifiers = true;
                                break;

                            default:
                                sb.Append(ch);
                                break;
                        }
                    }
                    else if (inModifiers)
                    {
                        switch (ch)
                        {
                            case '~':
                                modifierFuzzyMatch = !fuzzyMatch;
                                break;

                            case '?':
                                modifierOrdinality = Ordinality.ZeroOrOne;
                                break;

                            case '+':
                                modifierOrdinality = Ordinality.OneOrMore;
                                break;

                            case '*':
                                modifierOrdinality = Ordinality.ZeroOrMore;
                                break;

                            default:
                                if (variations.Any())
                                {
                                    FinishVariations(exactAnalyzer, fuzzyAnalyzer, sequence, modifierFuzzyMatch, modifierOrdinality, variations);
                                    inVariations = false;
                                    inModifiers = false;
                                    modifierOrdinality = Ordinality.One;
                                    variations.Clear();
                                    sb.Clear();
                                }
                                break;
                        }
                    }
                }
            }

            if (inVariations)
            {
                if (inModifiers && variations.Any())
                {
                    FinishVariations(exactAnalyzer, fuzzyAnalyzer, sequence, modifierFuzzyMatch, modifierOrdinality, variations);
                }
                else
                {
                    throw new Exception("Closing paren not found!");
                }
            }

            if (sb.Length > 0)
            {
                string text = sb.ToString().Trim();
                if (!String.IsNullOrEmpty(text))
                {
                    if (fuzzyMatch)
                    {
                        sequence.PatternMatchers.Add(CreateFuzzyTextPatternMatcher(text, fuzzyAnalyzer));
                    }
                    else
                    {
                        sequence.PatternMatchers.Add(CreateTextPatternMatcher(text, exactAnalyzer));
                    }
                }
            }

            Trace.TraceInformation($"{pattern}:\n\t{sequence}");
            return sequence;
        }

        private static void FinishVariations(Analyzer exactAnalyzer, Analyzer fuzzyAnalyzer, SequencePatternMatcher sequence, bool modifierFuzzyMatch, Ordinality modifierOrdinality, List<string> variations)
        {
            switch (modifierOrdinality)
            {
                case Ordinality.ZeroOrOne:
                    sequence.PatternMatchers.Add(new ZeroOrOnePatternMatcher(CreateVariationsPatternMatchers(variations, exactAnalyzer, fuzzyAnalyzer, modifierFuzzyMatch)));
                    break;
                case Ordinality.ZeroOrMore:
                    sequence.PatternMatchers.Add(new ZeroOrMorePatternMatcher(CreateVariationsPatternMatchers(variations, exactAnalyzer, fuzzyAnalyzer, modifierFuzzyMatch)));
                    break;
                case Ordinality.One:
                    sequence.PatternMatchers.Add(new OnePatternMatcher(CreateVariationsPatternMatchers(variations, exactAnalyzer, fuzzyAnalyzer, modifierFuzzyMatch)));
                    break;
                case Ordinality.OneOrMore:
                    sequence.PatternMatchers.Add(new OneOrMorePatternMatcher(CreateVariationsPatternMatchers(variations, exactAnalyzer, fuzzyAnalyzer, modifierFuzzyMatch)));
                    break;
            }
        }

        private static List<PatternMatcher> CreateVariationsPatternMatchers(IEnumerable<string> variations, Analyzer exactAnalyzer, Analyzer fuzzyAnalyzer, bool fuzzy = false)
        {
            var patternMatchers = new List<PatternMatcher>();
            foreach (var variation in variations.Select(variation => variation.Trim()))
            {
                if (variation.FirstOrDefault() == '@')
                {
                    patternMatchers.Add(new EntityPatternMatcher(variation));
                }
                else
                {
                    if (fuzzy)
                    {
                        patternMatchers.Add(CreateFuzzyTextPatternMatcher(variation, fuzzyAnalyzer));
                    }
                    else
                    {
                        patternMatchers.Add(CreateTextPatternMatcher(variation, exactAnalyzer));
                    }
                }
            }
            return patternMatchers;
        }

        private static PatternMatcher CreateTextPatternMatcher(string text, Analyzer analyzer)
        {
            var sequence = new SequencePatternMatcher();
            using (TextReader reader = new StringReader(text))
            {
                using (var tokenStream = analyzer.GetTokenStream("name", reader))
                {
                    var termAtt = tokenStream.GetAttribute<ICharTermAttribute>();
                    var offsetAtt = tokenStream.GetAttribute<IOffsetAttribute>();
                    tokenStream.Reset();

                    while (tokenStream.IncrementToken())
                    {
                        string token = termAtt.ToString();
                        sequence.PatternMatchers.Add(new TextPatternMatcher(token));
                    }
                }
            }

            if (sequence.PatternMatchers.Count == 1)
            {
                return sequence.PatternMatchers.First();
            }

            return sequence;
        }

        private static PatternMatcher CreateFuzzyTextPatternMatcher(string text, Analyzer analyzer)
        {
            var sequence = new SequencePatternMatcher();
            OnePatternMatcher oneOf = null;
            var start = -1;
            using (TextReader reader = new StringReader(text))
            {
                using (var tokenStream = analyzer.GetTokenStream("name", reader))
                {
                    var termAtt = tokenStream.GetAttribute<ICharTermAttribute>();
                    var offsetAtt = tokenStream.GetAttribute<IOffsetAttribute>();
                    tokenStream.Reset();

                    while (tokenStream.IncrementToken())
                    {
                        string token = termAtt.ToString();
                        var offset = offsetAtt.StartOffset;
                        if (start != offset)
                        {
                            start = offset;
                            if (oneOf != null)
                            {
                                sequence.PatternMatchers.Add(oneOf);
                            }
                            oneOf = new OnePatternMatcher();
                        }

                        oneOf.PatternMatchers.Add(new FuzzyTextPatternMatcher(token));
                    }

                    if (oneOf != null && oneOf.PatternMatchers.Any())
                    {
                        sequence.PatternMatchers.Add(oneOf);
                    }
                }
            }
            if (sequence.PatternMatchers.Count == 1)
            {
                return sequence.PatternMatchers.First();
            }
            return sequence;
        }

    }
}
