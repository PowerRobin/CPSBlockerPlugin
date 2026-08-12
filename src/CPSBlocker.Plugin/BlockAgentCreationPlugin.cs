using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;

namespace CPSBlocker.Plugin
{
    /// <summary>
    /// Blocks creation of Copilot Studio agents (bot table) based on environment variables.
    /// Register on: Message=Create, Entity=bot, Stage=PreValidation (10), Mode=Synchronous.
    ///
    /// Harness type is derived from the bot "template" column:
    ///   - GitHub Copilot Harness -> template starts with "cliagent" (e.g. cliagent-1.0.0)
    ///   - Standard Harness       -> every other template (e.g. default-2.1.0)
    /// </summary>
    public class BlockAgentCreationPlugin : PluginBase
    {
        private const string BlockStandardVarName = "cpsb_BlockStandardHarnessAgents";
        private const string BlockGitHubCopilotVarName = "cpsb_BlockGitHubCopilotHarnessAgents";
        private const string BlockSolutionImportedVarName = "cpsb_BlockSolutionImportedAgents";
        private const string CustomMessageVarName = "cpsb_AgentCreationBlockedMessage";

        private const string GitHubCopilotTemplatePrefix = "cliagent";

        public BlockAgentCreationPlugin(string unsecureConfiguration, string secureConfiguration)
            : base(typeof(BlockAgentCreationPlugin))
        {
        }

        protected override void ExecuteDataversePlugin(ILocalPluginContext localPluginContext)
        {
            if (localPluginContext == null)
            {
                throw new ArgumentNullException(nameof(localPluginContext));
            }

            var context = localPluginContext.PluginExecutionContext;

            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
            {
                return;
            }

            if (!string.Equals(target.LogicalName, "bot", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var service = localPluginContext.PluginUserService;

            bool blockStandard = GetBooleanEnvironmentVariable(service, localPluginContext, BlockStandardVarName);
            bool blockGitHubCopilot = GetBooleanEnvironmentVariable(service, localPluginContext, BlockGitHubCopilotVarName);

            if (!blockStandard && !blockGitHubCopilot)
            {
                return;
            }

            string template = target.GetAttributeValue<string>("template") ?? string.Empty;
            bool isGitHubCopilotHarness = template.StartsWith(GitHubCopilotTemplatePrefix, StringComparison.OrdinalIgnoreCase);

            localPluginContext.Trace(
                $"Agent creation attempt. Name='{target.GetAttributeValue<string>("name")}', template='{template}', " +
                $"isGitHubCopilotHarness={isGitHubCopilotHarness}, blockStandard={blockStandard}, blockGitHubCopilot={blockGitHubCopilot}");

            bool shouldBlock = isGitHubCopilotHarness ? blockGitHubCopilot : blockStandard;

            if (!shouldBlock)
            {
                return;
            }

            // Agents that arrive through a solution import (e.g. Microsoft first-party agents shipped via
            // managed solutions) are exempt unless the admin explicitly opts in to blocking them too.
            if (IsSolutionImportContext(context)
                && !GetBooleanEnvironmentVariable(service, localPluginContext, BlockSolutionImportedVarName))
            {
                localPluginContext.Trace("Skipping: bot Create is part of a solution import and import blocking is disabled.");
                return;
            }

            string customMessage = GetStringEnvironmentVariable(service, localPluginContext, CustomMessageVarName);
            string message = BuildErrorMessage(blockStandard, blockGitHubCopilot, customMessage);

            throw new InvalidPluginExecutionException(message);
        }

        /// <summary>
        /// Detects whether the current Create is running as part of a solution import by walking the
        /// plugin execution context parent chain for an ImportSolution message. Solution-imported
        /// agents (including Microsoft first-party agents) must not be blocked.
        /// </summary>
        private static bool IsSolutionImportContext(IPluginExecutionContext context)
        {
            for (IPluginExecutionContext ctx = context; ctx != null; ctx = ctx.ParentContext)
            {
                string message = ctx.MessageName ?? string.Empty;
                if (message.StartsWith("ImportSolution", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildErrorMessage(bool blockStandard, bool blockGitHubCopilot, string customMessage)
        {
            string scope;
            if (blockStandard && blockGitHubCopilot)
            {
                scope = "all";
            }
            else if (blockGitHubCopilot)
            {
                scope = "GitHub Copilot Harness";
            }
            else
            {
                scope = "Standard Harness";
            }

            string message = $"Creating of {scope} Agents is blocked in this environment.";

            if (!string.IsNullOrWhiteSpace(customMessage))
            {
                message += " " + customMessage.Trim();
            }

            return message;
        }

        private static bool GetBooleanEnvironmentVariable(IOrganizationService service, ILocalPluginContext ctx, string schemaName)
        {
            string value = GetEnvironmentVariable(service, ctx, schemaName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // Boolean environment variables store their value as the strings "true"/"false" or "yes"/"no".
            return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Trim() == "1";
        }

        private static string GetStringEnvironmentVariable(IOrganizationService service, ILocalPluginContext ctx, string schemaName)
        {
            return GetEnvironmentVariable(service, ctx, schemaName);
        }

        /// <summary>
        /// Resolves an environment variable's current value, falling back to the default value on the definition.
        /// </summary>
        private static string GetEnvironmentVariable(IOrganizationService service, ILocalPluginContext ctx, string schemaName)
        {
            var query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue", "schemaname"),
                Criteria = new FilterExpression(),
                TopCount = 1
            };
            query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);

            var valueLink = new LinkEntity(
                "environmentvariabledefinition",
                "environmentvariablevalue",
                "environmentvariabledefinitionid",
                "environmentvariabledefinitionid",
                JoinOperator.LeftOuter)
            {
                Columns = new ColumnSet("value"),
                EntityAlias = "v"
            };
            query.LinkEntities.Add(valueLink);

            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count == 0)
            {
                ctx.Trace($"Environment variable definition '{schemaName}' not found.");
                return null;
            }

            var definition = results.Entities[0];

            if (definition.Contains("v.value") && definition["v.value"] is AliasedValue aliased && aliased.Value is string currentValue)
            {
                return currentValue;
            }

            return definition.GetAttributeValue<string>("defaultvalue");
        }
    }
}
