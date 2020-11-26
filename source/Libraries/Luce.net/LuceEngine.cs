﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Luce.PatternMatchers;
using Luce.PatternMatchers.Matchers;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Ar;
using Lucene.Net.Analysis.Ca;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Cz;
using Lucene.Net.Analysis.Da;
using Lucene.Net.Analysis.De;
using Lucene.Net.Analysis.El;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Es;
using Lucene.Net.Analysis.Eu;
using Lucene.Net.Analysis.Fa;
using Lucene.Net.Analysis.Fi;
using Lucene.Net.Analysis.Fr;
using Lucene.Net.Analysis.Ga;
using Lucene.Net.Analysis.Gl;
using Lucene.Net.Analysis.Hi;
using Lucene.Net.Analysis.Hu;
using Lucene.Net.Analysis.Hy;
using Lucene.Net.Analysis.Id;
using Lucene.Net.Analysis.It;
using Lucene.Net.Analysis.Lv;
using Lucene.Net.Analysis.Nl;
using Lucene.Net.Analysis.No;
using Lucene.Net.Analysis.Phonetic;
using Lucene.Net.Analysis.Phonetic.Language.Bm;
using Lucene.Net.Analysis.Pt;
using Lucene.Net.Analysis.Ro;
using Lucene.Net.Analysis.Ru;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Sv;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Analysis.Tr;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Util;
using Newtonsoft.Json.Linq;
using builtin = Microsoft.Recognizers.Text;

namespace Luce
{
    /// <summary>
    /// LuceEngine uses a LuceModel to do LU pattern matching for entities.
    /// </summary>
    public class LuceEngine
    {
        private LuceModel _luceModel;
        private Analyzer _simpleAnalyzer = new SimpleAnalyzer(LuceneVersion.LUCENE_48);
        private Analyzer _exactAnalyzer;
        private Analyzer _fuzzyAnalyzer;

        private static HashSet<string> builtinEntities { get; set; } = new HashSet<string>()
        {
            "age", "boolean", "currency", "datetime", "dimension", "email", "guid", "hashtag",
            "ip", "mention", "number", "numberrange", "ordinal", "percentage", "phonenumber", "temperature", "url"
        };

        public LuceEngine(LuceModel model, Analyzer exactAnalyzer = null, Analyzer fuzzyAnalyzer = null)
        {
            this._luceModel = model;

            this._exactAnalyzer = exactAnalyzer ?? GetAnalyzerForLocale(model.Locale);

            this._fuzzyAnalyzer = exactAnalyzer ?? fuzzyAnalyzer ??
                Analyzer.NewAnonymous((field, textReader) =>
                {
                    Tokenizer tokenizer = new StandardTokenizer(LuceneVersion.LUCENE_48, textReader);
                    TokenStream stream = new DoubleMetaphoneFilter(tokenizer, 6, false);
                    //TokenStream stream = new BeiderMorseFilterFactory(new Dictionary<string, string>()
                    //    {
                    //        { "nameType", NameType.GENERIC.ToString()},
                    //        { "ruleType", RuleType.APPROX.ToString() },
                    //        { "languageSet", "auto"}
                    //    }).Create(tokenizer);
                    return new TokenStreamComponents(tokenizer, stream);
                });

            LoadModel();
            ValidateModel();
        }

        /// <summary>
        /// Turn on all built in entity recognizers
        /// </summary>
        /// <remarks>
        /// The default is to only run recognizers if you are referencing the entity types for the recognizer.
        /// When authoring it's useful to "turn them all on" for discovery.
        /// </remarks>
        public void UseAllBuiltEntities()
        {
            BuiltinEntities = new HashSet<string>(builtinEntities);
        }

        /// <summary>
        /// The locale for this model
        /// </summary>
        public string Locale { get; set; } = "en";

        public HashSet<string> BuiltinEntities { get; set; } = new HashSet<string>();

        /// <summary>
        /// Patterns to match
        /// </summary>
        public List<EntityPattern> EntityPatterns { get; set; } = new List<EntityPattern>();

        /// <summary>
        /// Wildcard Patterns to match
        /// </summary>
        public List<EntityPattern> WildcardEntityPatterns { get; set; } = new List<EntityPattern>();

        /// <summary>
        /// Warning messages
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// Error messages
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// Match entities in given text
        /// </summary>
        /// <param name="text">text to match against.</param>
        /// <param name="culture">culture</param>
        /// <param name="externalEntities">externally provided entities</param>
        /// <param name="includeInternal">include tokens in results</param>
        /// <returns>entities</returns>
        public IEnumerable<LuceEntity> MatchEntities(string text, string culture = "en", IEnumerable<LuceEntity> externalEntities = null, bool includeInternal = false)
        {
            var context = new MatchContext()
            {
                Text = text
            };

            if (externalEntities != null)
            {
                foreach (var externalEntity in externalEntities)
                {
                    context.Entities.Add(externalEntity);
                }
            }

            if (this.BuiltinEntities.Any())
            {
                AddBuiltInEntities(context, text, culture);
            }

            // add all @Token entities
            foreach (var tokenEntity in Tokenize(text))
            {
                context.TokenEntities.Add(tokenEntity);
            }

            int count = 0;
            do
            {
                count = context.Entities.Count;

                // foreach text token
                foreach (var textEntity in context.TokenEntities)
                {
                    // foreach entity pattern
                    foreach (var entityPattern in EntityPatterns)
                    {
                        ProcessEntityPattern(context, textEntity, entityPattern);
                    }
                }

                if (count == context.Entities.Count && WildcardEntityPatterns.Any())
                {
                    // process wildcard patterns
                    foreach (var textEntity in context.TokenEntities)
                    {
                        foreach (var entityPattern in WildcardEntityPatterns)
                        {
                            ProcessEntityPattern(context, textEntity, entityPattern);
                        }
                    }
                }
            } while (count != context.Entities.Count);

            // filter out internal entities
            if (includeInternal)
            {
                var merged = new List<LuceEntity>(context.TokenEntities);
                merged.AddRange(context.Entities);
                return merged;
            }

            // merge entities which are overlapping.
            var mergedEntities = new HashSet<LuceEntity>(new EntityTokenComparer());
            foreach (var entity1 in context.Entities.Where(entity => entity.Type[0] != '^'))
            {
                var alternateEntities = context.Entities.Where(e => e.Type == entity1.Type && e != entity1 && !mergedEntities.Contains(entity1)).ToList();
                if (alternateEntities.Count() == 0)
                {
                    mergedEntities.Add(entity1);
                }
                else
                {
                    // if no alterantes say "don't keep it" then we add it
                    if (!alternateEntities.Any(entity2 => ShouldDropEntity(entity1, entity2)))
                    {
                        mergedEntities.Add(entity1);
                    }
                }
            }

            return mergedEntities;
        }

        public IEnumerable<LuceEntity> Tokenize(string text)
        {
            using (var tokenStream = _exactAnalyzer.GetTokenStream("name", text))
            {
                var termAtt = tokenStream.GetAttribute<ICharTermAttribute>();
                var offsetAtt = tokenStream.GetAttribute<IOffsetAttribute>();
                tokenStream.Reset();

                while (tokenStream.IncrementToken())
                {
                    string token = termAtt.ToString();
                    bool skipFuzzy = false;
                    var start = offsetAtt.StartOffset;
                    var end = offsetAtt.EndOffset;

                    if (start > 0 && text[start - 1] == '@')
                    {
                        token = text.Substring(start - 1, end - start + 1);
                        skipFuzzy = true;
                    }
                    else if (start > 0 && text[start - 1] == '$')
                    {
                        token = text.Substring(start - 1, end - start + 1);
                        skipFuzzy = true;
                    }

                    var resolution = new TokenResolution()
                    {
                        Token = token
                    };

                    var luceEntity = new LuceEntity()
                    {
                        Type = TokenPatternMatcher.ENTITYTYPE,
                        Text = text.Substring(start, end - start),
                        Start = offsetAtt.StartOffset,
                        End = end,
                        Resolution = resolution
                    };

                    if (_fuzzyAnalyzer != null && !skipFuzzy)
                    {
                        // get fuzzyText
                        using (var fuzzyTokenStream = _fuzzyAnalyzer.GetTokenStream("name", luceEntity.Text))
                        {
                            var fuzzyTermAtt = fuzzyTokenStream.GetAttribute<ICharTermAttribute>();
                            fuzzyTokenStream.Reset();
                            while (fuzzyTokenStream.IncrementToken())
                            {
                                resolution.FuzzyTokens.Add(fuzzyTermAtt.ToString());
                            }
                        }
                    }

                    yield return luceEntity;
                }
            }
        }

        /// <summary>
        /// Format entities as fixed width string.
        /// </summary>
        /// <param name="text">original text</param>
        /// <param name="entities">entities</param>
        /// <returns></returns>
        public static string VisualizeResultsAsSpans(string text, IEnumerable<LuceEntity> entities)
        {
            if (!entities.Any())
            {
                return "No entities found";
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(text);
            foreach (var grp in entities.GroupBy(entity => entity.Type.ToLower())
                                                .OrderBy(grp => grp.Max(v => v.End - v.Start))
                                                .ThenBy(grp => grp.Min(v => v.Start)))
            {
                sb.Append(FormatEntityLine(text, grp));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Format entities as fixed width string.
        /// </summary>
        /// <param name="text">original text</param>
        /// <param name="entities">entities</param>
        /// <returns></returns>
        public static string VizualizeResultsAsHierarchy(string text, IEnumerable<LuceEntity> entities)
        {
            if (!entities.Any())
            {
                return "No entities found";
            }

            StringBuilder sb = new StringBuilder();

            foreach (var entity in entities.OrderByDescending(entity => entity.GetAllEntities().Count()))
            {
                sb.AppendLine(FormatEntityChildren(string.Empty, entity));
            }

            return sb.ToString();
        }

        private static string FormatEntityChildren(string prefix, LuceEntity entity)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"{prefix} @{entity}");

            if (String.IsNullOrEmpty(prefix))
            {
                prefix = "=> ";
            }

            foreach (var child in entity.Children)
            {
                sb.Append(FormatEntityChildren("    " + prefix, child));
            }
            return sb.ToString();
        }

        private static string FormatEntityLine(string text, IEnumerable<LuceEntity> entities)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var tier in ArrangeEntitiesIntoNonOverlappingTies(entities))
            {
                int lastEnd = 0;
                foreach (var entity in tier)
                {
                    sb.Append(new String(' ', entity.Start - lastEnd));
                    if (entity.End == entity.Start + 1)
                    {
                        sb.Append("^");
                    }
                    else
                    {
                        sb.Append($"^{new String('_', entity.End - entity.Start - 2)}^");
                    }
                    lastEnd = entity.End;
                }
                sb.AppendLine($"{new String(' ', text.Length - lastEnd)} @{entities.First().Type}");
            }

            return sb.ToString();
        }

        private static List<List<LuceEntity>> ArrangeEntitiesIntoNonOverlappingTies(IEnumerable<LuceEntity> entities)
        {
            var candidates = entities.ToList();
            var tiers = new List<List<LuceEntity>>();

            List<LuceEntity> tier = new List<LuceEntity>();
            List<LuceEntity> nextTier = new List<LuceEntity>();
            while (true)
            {
                foreach (var entity in candidates)
                {
                    // if collision
                    if (tier.Any(tierEntity =>
                        String.Equals(tierEntity.Type, entity.Type, StringComparison.OrdinalIgnoreCase) &&
                        (
                            // inside 
                            (tierEntity.Start >= entity.Start && tierEntity.End <= entity.End) ||
                            // overlaps on start
                            (tierEntity.Start <= entity.Start && tierEntity.End >= entity.Start && tierEntity.End <= entity.End) ||
                            // overlaps on End
                            (tierEntity.Start >= entity.Start && tierEntity.Start <= entity.End && tierEntity.End >= entity.End) ||
                            // bigger overlap
                            (tierEntity.Start <= entity.Start && tierEntity.End >= entity.End)
                        )))
                    {
                        nextTier.Add(entity);
                    }
                    else
                    {
                        tier.Add(entity);
                    }
                }

                tiers.Add(tier.OrderBy(e => e.Start).ToList());

                if (nextTier.Any())
                {
                    candidates = nextTier;
                    tier = new List<LuceEntity>();
                    nextTier = new List<LuceEntity>();
                }
                else
                {
                    return tiers;
                }
            }
        }

        private void ProcessEntityPattern(MatchContext context, LuceEntity textEntity, EntityPattern entityPattern)
        {
            context.EntityPattern = entityPattern;
            context.CurrentEntity = new LuceEntity()
            {
                Type = entityPattern.Name,
                Resolution = entityPattern.Resolution,
                Start = textEntity.Start
            };

            // see if it matches at this textEntity starting position.
            var matchResult = entityPattern.PatternMatcher.Matches(context, textEntity.Start);
            //System.Diagnostics.Trace.TraceInformation($"[{textEntity.Start}] {context.EntityPattern} => {matchResult.Matched}");

            // if it matches
            if (matchResult.Matched && matchResult.NextStart != textEntity.Start)
            {
                // add it to the entities.
                context.CurrentEntity.End = matchResult.NextStart;
                if (context.CurrentEntity.Resolution == null && !context.CurrentEntity.Children.Any())
                {
                    context.CurrentEntity.Resolution = context.Text.Substring(context.CurrentEntity.Start, context.CurrentEntity.End - context.CurrentEntity.Start);
                }

                if (!context.Entities.Contains(context.CurrentEntity))
                {
                    context.Entities.Add(context.CurrentEntity);
                    // Trace.TraceInformation($"\n [{textEntity.Start}] {context.EntityPattern} => {matchResult.Matched} {context.CurrentEntity}");
                }

                foreach (var childEntity in context.CurrentEntity.Children)
                {
                    if (!context.Entities.Contains(childEntity))
                    {
                        context.Entities.Add(childEntity);
                    }
                }
            }
        }

        private void LoadModel()
        {
            if (_luceModel.Macros == null)
            {
                _luceModel.Macros = new Dictionary<string, string>();
            }

            if (_luceModel.Entities != null)
            {
                foreach (var entityModel in _luceModel.Entities)
                {
                    foreach (var patternModel in entityModel.Patterns)
                    {
                        var resolution = entityModel.Patterns.Any(p => p.IsNormalized()) ? patternModel.First() : null;
                        foreach (var pattern in patternModel)
                        {
                            var expandedPattern = ExpandMacros(pattern);

                            var patternMatcher = PatternMatcher.Parse(expandedPattern, this._exactAnalyzer, this._fuzzyAnalyzer, entityModel.FuzzyMatch);
                            if (patternMatcher != null)
                            {
                                // Trace.TraceInformation($"{expandedPattern} => {patternMatcher}");
                                if (expandedPattern.Contains("___"))
                                {
                                    // we want to process wildcard patterns last
                                    WildcardEntityPatterns.Add(new EntityPattern(entityModel.Name, resolution, patternMatcher));
                                }
                                else
                                {
                                    EntityPatterns.Add(new EntityPattern(entityModel.Name, resolution, patternMatcher));
                                }
                            }
                        }
                    }
                }

                // add default pattern for datetime = (all permutations of datetime)
                EntityPatterns.Add(new EntityPattern("datetime", PatternMatcher.Parse("(@datetimeV2.date|@datetimeV2.time|@datetimeV2.datetime|@datetimeV2.daterange|@datetimeV2.timerange|@datetimeV2.datetimerange|@datetimeV2.duration)", this._exactAnalyzer, this._fuzzyAnalyzer)));

                // Auto detect all references to built in entities
                foreach (var pattern in this.EntityPatterns)
                {
                    foreach (var reference in pattern.PatternMatcher.GetEntityReferences().Select(r => r.TrimStart('@')))
                    {
                        if (builtinEntities.Contains(reference) ||
                            builtinEntities.Contains(reference.Split('.').First()))
                        {
                            this.BuiltinEntities.Add(reference);
                        }
                        else if (reference == "datetime" || reference == "datetimev2")
                        {
                            this.BuiltinEntities.Add("datetime");
                        }
                    }
                }
            }
        }

        private void ValidateModel()
        {
            HashSet<string> entityDefinitions = new HashSet<string>(builtinEntities)
            {
                "datetime", "datetimeV2.date", "datetimeV2.time", "datetimeV2.datetime",
                "datetimeV2.daterange", "datetimeV2.timerange", "datetimeV2.datetimerange",
                "datetimeV2.duration", "ordinal.relative", "wildcard"
            };

            foreach (var externlEntity in this._luceModel.ExternalEntities)
            {
                entityDefinitions.Add(externlEntity);
            }

            HashSet<string> entityReferences = new HashSet<string>();
            foreach (var pattern in this.EntityPatterns)
            {
                entityDefinitions.Add(pattern.Name);
                foreach (var reference in pattern.PatternMatcher.GetEntityReferences())
                {
                    entityReferences.Add(reference);
                }
            }

            foreach (var pattern in this.WildcardEntityPatterns)
            {
                entityDefinitions.Add(pattern.Name);
                foreach (var reference in pattern.PatternMatcher.GetEntityReferences())
                {
                    entityReferences.Add(reference);
                }
            }

            foreach (var entityRef in entityReferences)
            {
                if (!entityDefinitions.Contains(entityRef))
                {
                    Warnings.Add($"WARNING: @{entityRef} does not exist.");
                }
            }
        }

        private class Occurence
        {
            public int Start { get; set; }
            public int End { get; set; }
            public string Value { get; set; }
        }

        private string ExpandMacros(string pattern)
        {
            using (var tokenStream = _simpleAnalyzer.GetTokenStream("name", pattern))
            {
                var termAtt = tokenStream.GetAttribute<ICharTermAttribute>();
                var offsetAtt = tokenStream.GetAttribute<IOffsetAttribute>();
                tokenStream.Reset();

                Stack<Occurence> occurences = new Stack<Occurence>();
                while (tokenStream.IncrementToken())
                {
                    var start = offsetAtt.StartOffset;
                    var end = offsetAtt.EndOffset;

                    // if a $ reference
                    if (start > 0 && pattern[start - 1] == '$')
                    {
                        start--;
                        var macroName = pattern.Substring(start, end - start);
                        if (this._luceModel.Macros.TryGetValue(macroName, out string value))
                        {
                            occurences.Push(new Occurence() { Start = start, Value = value, End = end });
                        }
                        else
                        {
                            Warnings.Add($"WARNING: {macroName} is not defined.");
                        }
                    }
                }

                while (occurences.Count > 0)
                {
                    var occurence = occurences.Pop();
                    pattern = $"{pattern.Substring(0, occurence.Start)}{occurence.Value}{pattern.Substring(occurence.End)}";
                }
            }

            return pattern;
        }

        private void AddBuiltInEntities(MatchContext context, string text, string culture)
        {
            List<builtin.ModelResult> results = null;
            foreach (var name in BuiltinEntities)
            {
                switch (name)
                {
                    case "age":
                        results = builtin.NumberWithUnit.NumberWithUnitRecognizer.RecognizeAge(text, culture);
                        break;
                    case "boolean":
                        results = builtin.Choice.ChoiceRecognizer.RecognizeBoolean(text, culture); ;
                        break;
                    case "currency":
                        results = builtin.NumberWithUnit.NumberWithUnitRecognizer.RecognizeCurrency(text, culture);
                        break;
                    case "datetime":
                        results = builtin.DateTime.DateTimeRecognizer.RecognizeDateTime(text, culture);
                        break;
                    case "dimension":
                        results = builtin.NumberWithUnit.NumberWithUnitRecognizer.RecognizeDimension(text, culture);
                        break;
                    case "email":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeEmail(text, culture);
                        break;
                    case "guid":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeGUID(text, culture);
                        break;
                    case "hashtag":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeHashtag(text, culture);
                        break;
                    case "ip":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeIpAddress(text, culture);
                        break;
                    case "mention":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeMention(text, culture);
                        break;
                    case "number":
                        results = builtin.Number.NumberRecognizer.RecognizeNumber(text, culture);
                        break;
                    case "numberrange":
                        results = builtin.Number.NumberRecognizer.RecognizeNumberRange(text, culture);
                        break;
                    case "ordinal":
                        results = builtin.Number.NumberRecognizer.RecognizeOrdinal(text, culture);
                        break;
                    case "percentage":
                        results = builtin.Number.NumberRecognizer.RecognizePercentage(text, culture);
                        break;
                    case "phonenumber":
                        results = builtin.Sequence.SequenceRecognizer.RecognizePhoneNumber(text, culture);
                        break;
                    case "temperature":
                        results = builtin.NumberWithUnit.NumberWithUnitRecognizer.RecognizeTemperature(text, culture); ;
                        break;
                    case "url":
                        results = builtin.Sequence.SequenceRecognizer.RecognizeURL(text, culture);
                        break;
                }

                foreach (var result in results)
                {
                    context.Entities.Add(new LuceEntity()
                    {
                        Text = result.Text,
                        Type = result.TypeName,
                        Start = result.Start,
                        End = result.End + 1,
                        Resolution = result.Resolution,
                        Score = 1.0F
                    });
                }
            }
        }

        private bool ShouldDropEntity(LuceEntity entity1, LuceEntity entity2)
        {
            // if entity2 is bigger on both ends
            if (entity2.Start < entity1.Start && entity2.End > entity1.End)
            {
                return true;
            }

            // if it's inside the current token
            if (entity2.Start >= entity1.Start && entity2.End <= entity1.End)
            {
                return false;
            }
            // if offset overlapping at start or end
            if ((entity2.Start <= entity1.Start && entity2.End >= entity1.Start && entity2.End <= entity1.End) ||
                (entity2.Start >= entity1.Start && entity2.Start < entity1.End && entity2.End >= entity1.End))
            {
                var entity1Length = entity1.End - entity1.Start;
                var entity2Length = entity2.End - entity2.Start;
                if (entity1Length > entity2Length)
                {
                    return false;
                }
                else if (entity2Length > entity1Length)
                {
                    return true;
                }
            }
            return false;
        }

        private Analyzer GetAnalyzerForLocale(string locale = "en")
        {
            var language = CultureInfo.GetCultureInfo(locale).EnglishName.Split(' ').First();
            switch (language)
            {
                case "Arabic":
                    return new ArabicAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Armenian":
                    return new ArmenianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Basque":
                    return new BasqueAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Catalan":
                    return new CatalanAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Chinese":
                    return new StandardAnalyzer(LuceneVersion.LUCENE_48, stopWords: CharArraySet.EMPTY_SET);
                case "Czech":
                    return new CzechAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Danish":
                    return new DanishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Dutch":
                    return new DutchAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "English":
                    return new EnglishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Finnish":
                    return new FinnishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "French":
                    return new FrenchAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Galician":
                    return new GalicianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "German":
                    return new GermanAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Greek":
                    return new GreekAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Hindi":
                    return new HindiAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Hungarian":
                    return new HungarianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Indonesian":
                    return new IndonesianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Irish":
                    return new IrishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Italian":
                    return new ItalianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Latvian":
                    return new LatvianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Norwegian":
                    return new NorwegianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Persian":
                    return new PersianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Portuguese":
                    return new PortugueseAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Romanian":
                    return new RomanianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Russian":
                    return new RussianAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Spanish":
                    return new SpanishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Swedish":
                    return new SwedishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                case "Turkish":
                    return new TurkishAnalyzer(LuceneVersion.LUCENE_48, stopwords: CharArraySet.EMPTY_SET);
                default:
                    return new StandardAnalyzer(LuceneVersion.LUCENE_48, stopWords: CharArraySet.EMPTY_SET);
            }
        }
    }
}

