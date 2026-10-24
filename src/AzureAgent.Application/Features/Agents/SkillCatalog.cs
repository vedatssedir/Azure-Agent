using System.Text.RegularExpressions;

namespace AzureAgent.Application.Features.Agents;

public sealed record CatalogSkill(string Key, string Name, string Description, string Content, IReadOnlyList<string> Signals);

public sealed record CatalogMcpServer(string Name, string Description, string Transport, string? Command, IReadOnlyList<string> Arguments, string? Url,
    IReadOnlyList<string> SecretNames);

/// <summary>Built-in skills and MCP servers, plus repository-based suggestions.</summary>
public static class SkillCatalog
{
    private const string Always = "*";

    public static readonly IReadOnlyList<CatalogSkill> Skills =
    [
        new("azure-work-item", "Azure work item workflow", "How to turn an Azure Boards work item into a reviewable change.",
            """
            Read the work item description and acceptance criteria first.
            Make the smallest change that satisfies them; do not refactor unrelated code.
            Add or update tests that prove the acceptance criteria.
            Run the repository's build and tests before finishing and report the results.
            If the work item is ambiguous, implement the safest interpretation and state the assumption in your summary.
            """, [Always]),
        new("security-review", "Security baseline", "Checks every change must pass.",
            """
            Never commit secrets, tokens or connection strings; use configuration or the existing secret store.
            Validate and encode untrusted input; use parameterized queries.
            Do not weaken authentication, authorization or TLS settings.
            """, [Always]),
        new("dotnet", ".NET", "Conventions for C# / .NET solutions.",
            """
            Build with `dotnet build` and test with `dotnet test` from the solution directory.
            Respect nullable reference types and analyzers; fix warnings you introduce.
            Use async APIs and dependency injection the way surrounding code does.
            """, [@"\.sln$", @"\.slnx$", @"\.csproj$", "^global\\.json$", "^Directory\\.Build\\.props$"]),
        new("node-typescript", "Node.js / TypeScript", "Conventions for Node.js and TypeScript repositories.",
            """
            Use the package manager the lockfile implies (pnpm-lock.yaml → pnpm, yarn.lock → yarn, otherwise npm).
            Prefer the scripts in package.json (test, lint, typecheck, build).
            Keep strict typing; do not add `any` or disable lint rules to make a change compile.
            """, ["^package\\.json$", "^tsconfig.*\\.json$"]),
        new("python", "Python", "Conventions for Python projects.",
            """
            Install from pyproject.toml or requirements files in a virtual environment.
            Run the configured test runner (pytest by default) and linters if present. Add type hints to new functions.
            """, ["^pyproject\\.toml$", "^requirements.*\\.txt$", "^setup\\.py$"]),
        new("java", "Java / JVM", "Conventions for Maven and Gradle projects.",
            "Use the wrapper (./mvnw or ./gradlew) when present. Run the unit tests for touched modules.",
            ["^pom\\.xml$", "^build\\.gradle(\\.kts)?$"]),
        new("frontend", "Web frontend", "UI changes in React, Angular or Vue.",
            """
            Reuse existing components and design tokens instead of adding new styles.
            Keep components accessible (labels, keyboard focus, contrast). Add component tests for changed behaviour.
            """, ["^angular\\.json$", "^vite\\.config\\.", "^next\\.config\\."]),
    ];

    public static readonly IReadOnlyList<CatalogMcpServer> McpServers =
    [
        new("azure-devops", "Microsoft's Azure DevOps MCP server: work items, repos, pipelines, wiki.", "Stdio", "npx",
            ["-y", "@azure-devops/mcp", "<organization>"], null, ["AZURE_DEVOPS_PAT"]),
        new("playwright", "Browser automation for verifying web UI changes.", "Stdio", "npx", ["-y", "@playwright/mcp@latest", "--headless"], null, []),
    ];

    public static IReadOnlyList<SkillSuggestionDto> Suggest(IReadOnlyDictionary<string, IReadOnlyList<string>> rootFilesByRepository, IReadOnlySet<string> installed)
    {
        var result = new List<SkillSuggestionDto>();
        foreach (var skill in Skills.Where(s => !installed.Contains(s.Key)))
        {
            if (skill.Signals.Contains(Always))
            {
                result.Add(new(skill.Key, skill.Name, skill.Description, "Recommended for every agent"));
                continue;
            }
            var repos = rootFilesByRepository.Where(r => r.Value.Any(f => skill.Signals.Any(s => Regex.IsMatch(f, s, RegexOptions.IgnoreCase))))
                .Select(r => r.Key).ToList();
            if (repos.Count > 0) result.Add(new(skill.Key, skill.Name, skill.Description, $"Detected in {string.Join(", ", repos)}"));
        }
        return result;
    }
}
