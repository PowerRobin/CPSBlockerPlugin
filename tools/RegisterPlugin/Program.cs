using System.Reflection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
string? urlArg = GetArg(args, "--url");
string? tenantId = GetArg(args, "--tenant");

if (string.IsNullOrWhiteSpace(urlArg) || string.IsNullOrWhiteSpace(tenantId))
{
    Console.Error.WriteLine("Usage: RegisterPlugin --url <Dataverse URL> --tenant <Entra tenant ID> [--dll <plugin DLL path>]");
    return 1;
}

string url = urlArg.TrimEnd('/');
string dllPath = GetArg(args, "--dll")
    ?? Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "CPSBlocker.Plugin", "bin", "Release", "net462", "CPSBlocker.Plugin.dll"));

const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"; // public client, supports device code

const string PublisherUniqueName = "CPSBlockerPublisher";
const string PublisherFriendlyName = "CPS Blocker Publisher";
const string Prefix = "cpsb";
const int OptionValuePrefix = 63847;

const string SolutionUniqueName = "CPSBlockerSolution";
const string SolutionFriendlyName = "CPS Agent Creation Blocker";

const string AssemblyName = "CPSBlocker.Plugin";
const string PluginTypeName = "CPSBlocker.Plugin.BlockAgentCreationPlugin";

// Environment variable definitions
var envVars = new (string SchemaName, string DisplayName, int Type, string DefaultValue, string Description)[]
{
    ("cpsb_BlockStandardHarnessAgents", "Block Standard Harness Agents", 100000002 /*Boolean*/, "false",
        "When Yes, blocks creation of Standard Harness (Copilot Studio) agents."),
    ("cpsb_BlockGitHubCopilotHarnessAgents", "Block GitHub Copilot Harness Agents", 100000002 /*Boolean*/, "false",
        "When Yes, blocks creation of GitHub Copilot Harness (CLI) agents."),
    ("cpsb_AgentCreationBlockedMessage", "Agent Creation Blocked Message", 100000000 /*String*/,
        "Please contact your Power Platform administrator if you require an exception.",
        "Custom message appended to the blocking error shown to the maker."),
};

if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"Plugin DLL not found at: {dllPath}");
    return 1;
}

Console.WriteLine($"Environment : {url}");
Console.WriteLine($"Tenant      : {tenantId}");
Console.WriteLine($"Plugin DLL  : {dllPath}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Authenticate (device code) and connect
// ---------------------------------------------------------------------------
var app = PublicClientApplicationBuilder
    .Create(AzureCliClientId)
    .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
    .WithDefaultRedirectUri()
    .Build();

string[] scopes = { $"{url}/.default" };

async Task<string> AcquireToken(string _)
{
    AuthenticationResult result;
    var accounts = await app.GetAccountsAsync();
    try
    {
        result = await app.AcquireTokenSilent(scopes, accounts.FirstOrDefault()).ExecuteAsync();
    }
    catch (MsalUiRequiredException)
    {
        result = await app.AcquireTokenWithDeviceCode(scopes, dc =>
        {
            Console.WriteLine();
            Console.WriteLine(dc.Message);
            Console.WriteLine();
            return Task.CompletedTask;
        }).ExecuteAsync();
    }
    return result.AccessToken;
}

using var svc = new ServiceClient(new Uri(url), AcquireToken, useUniqueInstance: true, logger: null);
if (!svc.IsReady)
{
    Console.Error.WriteLine($"Failed to connect: {svc.LastError}");
    return 1;
}
var whoAmI = (WhoAmIResponse)svc.Execute(new WhoAmIRequest());
Console.WriteLine($"Connected. User: {whoAmI.UserId}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// 0. Ensure System Administrator role (needed for pluginassembly privileges)
// ---------------------------------------------------------------------------
EnsureSystemAdministrator(whoAmI.UserId);

// ---------------------------------------------------------------------------
// 1. Publisher
// ---------------------------------------------------------------------------
Guid publisherId = EnsurePublisher();
Console.WriteLine($"Publisher   : {PublisherUniqueName} ({publisherId})");

// ---------------------------------------------------------------------------
// 2. Solution
// ---------------------------------------------------------------------------
EnsureSolution(publisherId);
Console.WriteLine($"Solution    : {SolutionUniqueName}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// 3. Environment variable definitions
// ---------------------------------------------------------------------------
foreach (var ev in envVars)
{
    Guid defId = EnsureEnvVarDefinition(ev.SchemaName, ev.DisplayName, ev.Type, ev.DefaultValue, ev.Description);
    AddToSolution(defId, 380 /*EnvironmentVariableDefinition*/);
    Console.WriteLine($"Env var     : {ev.SchemaName} ({defId})");
}
Console.WriteLine();

// ---------------------------------------------------------------------------
// 4. Plugin assembly, type, step
// ---------------------------------------------------------------------------
Guid assemblyId = EnsureAssembly(dllPath);
AddToSolution(assemblyId, 91 /*PluginAssembly*/);
Console.WriteLine($"Assembly    : {AssemblyName} ({assemblyId})");

Guid typeId = EnsurePluginType(assemblyId);
Console.WriteLine($"Plugin type : {PluginTypeName} ({typeId})");

Guid stepId = EnsureStep(typeId);
AddToSolution(stepId, 92 /*SdkMessageProcessingStep*/);
Console.WriteLine($"Step        : Create of bot (PreValidation, Sync) ({stepId})");
Console.WriteLine();

Console.WriteLine("Publishing customizations...");
svc.Execute(new Microsoft.Crm.Sdk.Messages.PublishAllXmlRequest());

Console.WriteLine();
Console.WriteLine("DONE. Solution '" + SolutionUniqueName + "' now contains the 3 environment variables and the plugin step.");
return 0;

// ===========================================================================
// Helpers
// ===========================================================================
void EnsureSystemAdministrator(Guid userId)
{
    var check = new QueryExpression("role")
    {
        ColumnSet = new ColumnSet("roleid"),
        Criteria = new FilterExpression()
    };
    check.Criteria.AddCondition("name", ConditionOperator.Equal, "System Administrator");
    var userRolesLink = check.AddLink("systemuserroles", "roleid", "roleid", JoinOperator.Inner);
    userRolesLink.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);
    if (svc.RetrieveMultiple(check).Entities.Any())
    {
        Console.WriteLine("System Administrator role already assigned.");
        Console.WriteLine();
        return;
    }

    var q = new QueryExpression("role")
    {
        ColumnSet = new ColumnSet("roleid", "name"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("name", ConditionOperator.Equal, "System Administrator");
    var adminRole = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
    if (adminRole == null)
    {
        Console.WriteLine("WARNING: 'System Administrator' role not found; skipping self-elevation.");
        Console.WriteLine();
        return;
    }

    try
    {
        svc.Associate(
            "systemuser",
            userId,
            new Relationship("systemuserroles_association"),
            new EntityReferenceCollection { new EntityReference("role", adminRole.Id) });
        Console.WriteLine("Assigned System Administrator role to the current user.");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Could not self-assign System Administrator role: " + ex.Message);
        Console.WriteLine("Grant 'System Administrator' to your user in the Power Platform Admin Center, then re-run.");
    }
    Console.WriteLine();
}

Guid EnsurePublisher()
{
    var q = new QueryExpression("publisher")
    {
        ColumnSet = new ColumnSet("publisherid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, PublisherUniqueName);
    var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
    if (existing != null) return existing.Id;

    var publisher = new Entity("publisher")
    {
        ["uniquename"] = PublisherUniqueName,
        ["friendlyname"] = PublisherFriendlyName,
        ["customizationprefix"] = Prefix,
        ["customizationoptionvalueprefix"] = OptionValuePrefix
    };
    return svc.Create(publisher);
}

void EnsureSolution(Guid publisherId)
{
    var q = new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("solutionid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, SolutionUniqueName);
    if (svc.RetrieveMultiple(q).Entities.Any()) return;

    var solution = new Entity("solution")
    {
        ["uniquename"] = SolutionUniqueName,
        ["friendlyname"] = SolutionFriendlyName,
        ["version"] = "1.0.0.0",
        ["publisherid"] = new EntityReference("publisher", publisherId),
        ["description"] = "Blocks creation of Standard Harness and/or GitHub Copilot Harness agents based on environment variables."
    };
    svc.Create(solution);
}

Guid EnsureEnvVarDefinition(string schemaName, string displayName, int type, string defaultValue, string description)
{
    var q = new QueryExpression("environmentvariabledefinition")
    {
        ColumnSet = new ColumnSet("environmentvariabledefinitionid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
    var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
    if (existing != null)
    {
        var upd = new Entity("environmentvariabledefinition", existing.Id)
        {
            ["defaultvalue"] = defaultValue,
            ["displayname"] = displayName,
            ["description"] = description
        };
        svc.Update(upd);
        return existing.Id;
    }

    var def = new Entity("environmentvariabledefinition")
    {
        ["schemaname"] = schemaName,
        ["displayname"] = displayName,
        ["description"] = description,
        ["type"] = new OptionSetValue(type),
        ["defaultvalue"] = defaultValue
    };
    return svc.Create(def);
}

Guid EnsureAssembly(string path)
{
    byte[] bytes = File.ReadAllBytes(path);
    string content = Convert.ToBase64String(bytes);
    var name = GetStrongName(path);

    var q = new QueryExpression("pluginassembly")
    {
        ColumnSet = new ColumnSet("pluginassemblyid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("name", ConditionOperator.Equal, AssemblyName);
    var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();

    var assembly = new Entity("pluginassembly")
    {
        ["name"] = AssemblyName,
        ["content"] = content,
        ["culture"] = name.Culture,
        ["version"] = name.Version,
        ["publickeytoken"] = name.PublicKeyToken,
        ["sourcetype"] = new OptionSetValue(0), // Database
        ["isolationmode"] = new OptionSetValue(2) // Sandbox
    };

    if (existing != null)
    {
        assembly.Id = existing.Id;
        svc.Update(assembly);
        return existing.Id;
    }
    return svc.Create(assembly);
}

Guid EnsurePluginType(Guid assemblyId)
{
    var q = new QueryExpression("plugintype")
    {
        ColumnSet = new ColumnSet("plugintypeid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("typename", ConditionOperator.Equal, PluginTypeName);
    q.Criteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);
    var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
    if (existing != null) return existing.Id;

    var type = new Entity("plugintype")
    {
        ["typename"] = PluginTypeName,
        ["friendlyname"] = PluginTypeName,
        ["name"] = "Block Agent Creation Plugin",
        ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId)
    };
    return svc.Create(type);
}

Guid EnsureStep(Guid typeId)
{
    Guid createMessageId = GetMessageId("Create");
    Guid? filterId = GetMessageFilterId(createMessageId, "bot");

    const string stepName = "CPSBlocker: Block agent (bot) creation on PreValidation";

    var q = new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("name", ConditionOperator.Equal, stepName);
    q.Criteria.AddCondition("plugintypeid", ConditionOperator.Equal, typeId);
    var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
    if (existing != null) return existing.Id;

    var step = new Entity("sdkmessageprocessingstep")
    {
        ["name"] = stepName,
        ["plugintypeid"] = new EntityReference("plugintype", typeId),
        ["sdkmessageid"] = new EntityReference("sdkmessage", createMessageId),
        ["stage"] = new OptionSetValue(10), // PreValidation
        ["mode"] = new OptionSetValue(0),   // Synchronous
        ["rank"] = 1,
        ["supporteddeployment"] = new OptionSetValue(0), // Server only
        ["invocationsource"] = new OptionSetValue(0),
        ["description"] = "Blocks agent creation based on environment variables."
    };
    if (filterId.HasValue)
    {
        step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);
    }
    return svc.Create(step);
}

Guid GetMessageId(string messageName)
{
    var q = new QueryExpression("sdkmessage")
    {
        ColumnSet = new ColumnSet("sdkmessageid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);
    return svc.RetrieveMultiple(q).Entities.First().Id;
}

Guid? GetMessageFilterId(Guid messageId, string entityLogicalName)
{
    var q = new QueryExpression("sdkmessagefilter")
    {
        ColumnSet = new ColumnSet("sdkmessagefilterid"),
        Criteria = new FilterExpression()
    };
    q.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
    q.Criteria.AddCondition("primaryobjecttypecode", ConditionOperator.Equal, entityLogicalName);
    return svc.RetrieveMultiple(q).Entities.FirstOrDefault()?.Id;
}

void AddToSolution(Guid componentId, int componentType)
{
    svc.Execute(new AddSolutionComponentRequest
    {
        ComponentId = componentId,
        ComponentType = componentType,
        SolutionUniqueName = SolutionUniqueName,
        AddRequiredComponents = false
    });
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}

static (string Culture, string Version, string PublicKeyToken) GetStrongName(string path)
{
    var an = System.Reflection.AssemblyName.GetAssemblyName(path);
    string culture = string.IsNullOrEmpty(an.CultureName) ? "neutral" : an.CultureName;
    string version = an.Version?.ToString() ?? "1.0.0.0";
    byte[]? tokenBytes = an.GetPublicKeyToken();
    string token = tokenBytes is { Length: > 0 }
        ? string.Concat(tokenBytes.Select(b => b.ToString("x2")))
        : "null";
    return (culture, version, token);
}
