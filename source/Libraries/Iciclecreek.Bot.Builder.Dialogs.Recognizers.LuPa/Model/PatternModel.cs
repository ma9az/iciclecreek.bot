﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lucene.Net.Analysis;
using Newtonsoft.Json.Linq;

namespace Iciclecreek.Bot.Builder.Dialogs.Recognizers.Lupa
{
    /// <summary>
    /// Represents a pattern which is a string, or array of strings
    /// </summary>
    public class PatternModel : IEnumerable<string>
    {
        private List<string> patterns = new List<string>();

        public PatternModel(string patternDefinition)
        {
            this.patterns.Add(patternDefinition.Trim());
        }

        public PatternModel(string[] patternDefinitions)
        {
            this.patterns.AddRange(patternDefinitions.Select(pattern => pattern.Trim()));
        }

        public bool IsNormalized()
        {
            return this.patterns.Count > 1;
        }

        public IEnumerator<string> GetEnumerator() => this.patterns.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)this.patterns).GetEnumerator();

        public static implicit operator PatternModel(string patternDefinition) => new PatternModel(patternDefinition);
        public static implicit operator PatternModel(JValue patternDefinition) => new PatternModel((string)patternDefinition);

        public static implicit operator PatternModel(string[] patternDefinitions) => new PatternModel(patternDefinitions);
        public static implicit operator PatternModel(JArray patternDefinitions) => new PatternModel(patternDefinitions.ToObject<string[]>());
    }
}
