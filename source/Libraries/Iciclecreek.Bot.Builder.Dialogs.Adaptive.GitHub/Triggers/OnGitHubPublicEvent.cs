using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using AdaptiveExpressions;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Conditions;
using Newtonsoft.Json;

namespace Iciclecreek.Bot.Builder.Dialogs.Adaptive.GitHub.Triggers
{
    /// <summary>
    /// Trigger on github 'public' webhook event.
    /// </summary>
	public class OnGitHubPublicEvent : OnGitHubEvent
    {
        /// <summary>
        /// Class identifier.
        /// </summary>
        [JsonProperty("$kind")]
        public new const string Kind = "Iciclecreek.OnGitHubPublicEvent";

        /// <summary>
        /// Initializes a new instance of the <see cref="OnGitHubPublicEvent"/> class.
        /// </summary>
        /// <param name="actions">Optional, list of <see cref="Dialog"/> actions.</param>
        /// <param name="condition">Optional, condition which needs to be met for the actions to be executed.</param>
        /// <param name="callerPath">Optional, source file full path.</param>
        /// <param name="callerLine">Optional, line number in source file.</param>
        [JsonConstructor]
        public OnGitHubPublicEvent(List<Dialog> actions = null, string condition = null, [CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = 0)
            : base(actions: actions, condition: condition, callerPath: callerPath, callerLine: callerLine)
        {
        }


        /// <summary>
        /// Gets this activity's representing expresion.
        /// </summary>
        /// <returns>An <see cref="Expression"/> representing the activity.</returns>
        public override Expression GetExpression()
        {
            Expression actionCondition;
            actionCondition = Expression.Parse("turn.activity.value.action == null");
            var propertyCondition = Expression.AndExpression(
                Expression.Parse("turn.activity.value.ref == null"),
                Expression.Parse("turn.activity.value.pusher_type == null"),
                Expression.Parse("turn.activity.value.master_branch == null"),
                Expression.Parse("turn.activity.value.repository != null"),
                Expression.Parse("turn.activity.value.sender != null"),
                Expression.Parse("turn.activity.value.organization == null")
            );
            return Expression.AndExpression(base.GetExpression(), actionCondition, propertyCondition);
        }
    }
}
