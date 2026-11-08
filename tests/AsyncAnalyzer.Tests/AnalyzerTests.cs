using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using Xunit.Abstractions;

namespace AsyncAnalyzer.Tests;

public sealed class AnalyzerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Blocking_calls_use_framework_members_and_the_nearest_function()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            class Custom
            {
                public int Result => 1;
                public void Wait() { }
                public Custom GetAwaiter() => this;
                public void GetResult() { }
            }
            class Sample
            {
                async Task Run(Task<int> task, Task other, ValueTask<int> value)
                {
                    await Task.Yield();
                    _ = [|task.Result|];
                    [|other.Wait(10)|];
                    _ = [|task.GetAwaiter().GetResult()|];
                    [|other.GetAwaiter().GetResult()|];
                    var custom = new Custom();
                    _ = custom.Result;
                    custom.Wait();
                    custom.GetAwaiter().GetResult();
                    _ = value.Result;
                    _ = value.GetAwaiter().GetResult();
                    Func<int> sync = () => task.Result;
                    int SyncLocal() => task.Result;
                    Func<Task<int>> asyncLambda = async () =>
                    {
                        await Task.Yield();
                        return [|task.Result|];
                    };
                    async Task<int> AsyncLocal()
                    {
                        await Task.Yield();
                        return [|task.Result|];
                    }
                }

                void Sync(Task task) => task.Wait();
            }
            """;

        await AssertDiagnostics(source, "AG001",
            "'Task.Result' blocks inside an async function",
            "'Task.Wait' blocks inside an async function",
            "'Task.GetAwaiter().GetResult()' blocks inside an async function",
            "'Task.GetAwaiter().GetResult()' blocks inside an async function",
            "'Task.Result' blocks inside an async function",
            "'Task.Result' blocks inside an async function");
    }

    [Fact]
    public async Task A_source_type_with_the_framework_name_is_not_a_task()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;

            namespace System.Threading.Tasks
            {
                class Task<T>
                {
                    public int Result => 1;
                }
            }

            class Sample
            {
                async Task Run(Task<int> custom)
                {
                    await Task.Yield();
                    _ = custom.Result;
                }
            }
            """);

        Assert.Empty(await Analyze(document));
    }

    [Fact]
    public async Task Async_void_event_exception_is_based_on_signature()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            class ChangedArgs : EventArgs { }
            class Sample
            {
                public async void [|Fire|]() => await Task.Yield();
                async void [|WrongSender|](string sender, EventArgs e) => await Task.Yield();
                async void [|WrongArgs|](object sender, object e) => await Task.Yield();
                async void Changed(object? source, ChangedArgs args) => await Task.Yield();
                void Register()
                {
                    async void [|Local|]() => await Task.Yield();
                    Action fire = [|async|] () => await Task.Yield();
                    EventHandler handler = async (sender, args) => await Task.Yield();
                }
                async Task Run() => await Task.Yield();
            }
            """;

        await AssertDiagnostics(source, "AG002", Enumerable.Repeat(
            "Use an awaitable return type for async functions outside event handlers", 5).ToArray());
    }

    [Fact]
    public async Task Missing_token_requires_a_single_visible_parameter_and_bound_call()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Sample
            {
                static Task Send<T>(T value, int priority = 0, CancellationToken ct = default) =>
                    Task.CompletedTask;
                static void Two(CancellationToken a = default, CancellationToken b = default) { }
                static void Ambiguous(int? number = null, CancellationToken ct = default) { }
                static void Ambiguous(long? number = null, CancellationToken ct = default) { }

                static async Task Run(CancellationToken token)
                {
                    await [|Send<int>|](priority: 4, value: 1);
                    await Send(1, ct: default);
                    Two();
                    Func<Task> nested = () => Send(1);
                    Func<CancellationToken, Task> own = other => [|Send|](1);
                    Task Local() => Send(1);
                    Task WithToken(CancellationToken local) => [|Send|](1);
                }

                static Task WithTwo(CancellationToken first, CancellationToken second) => Send(1);
                static Task WithNone() => Send(1);
                static void Broken(CancellationToken token) => Ambiguous();
            }
            """;

        await AssertDiagnostics(source, "AG003",
            "Pass 'token' to optional parameter 'ct'",
            "Pass 'other' to optional parameter 'ct'",
            "Pass 'local' to optional parameter 'ct'");
    }

    [Fact]
    public async Task Generated_and_suppressed_code_do_not_report()
    {
        const string source = """
            using System.Threading.Tasks;
            class Sample
            {
            #pragma warning disable AG002
                async void Quiet() => await Task.Yield();
            #pragma warning restore AG002
                async void [|Loud|]() => await Task.Yield();
            }
            """;

        const string message = "Use an awaitable return type for async functions outside event handlers";
        await AssertDiagnostics(source, "AG002", message);

        using var workspace = new AdhocWorkspace();
        var clean = source.Replace("[|", "").Replace("|]", "");
        Assert.Empty(await Analyze(CreateDocument(workspace, clean, "Sample.g.cs")));
        Assert.Empty(await Analyze(CreateDocument(workspace, "// <auto-generated/>\n" + clean)));
        Assert.Empty(await Analyze(CreateDocument(workspace, clean,
            editorConfig: "[*.cs]\ndotnet_diagnostic.AG002.severity = none")));
    }

    [Fact]
    public async Task Incomplete_code_does_not_crash_analysis()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading;
            using System.Threading.Tasks;
            class Sample
            {
                async void Broken(CancellationToken token, Task<int> task)
                {
                    var value = task.Result;
                    Missing(value:
            """);

        Assert.Equal(new[] { "AG002", "AG001" }, (await Analyze(document)).Select(d => d.Id));
    }

    [Fact]
    public async Task Result_fix_preserves_type_and_removes_the_diagnostic()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;
            class Sample
            {
                async Task<int> Read(Task<int> task)
                {
                    var value = task.Result; // used below
                    return value;
                }
            }
            """);

        var updated = await ApplyOnlyFix(document);
        Assert.Contains("var value = await task; // used below", (await updated.GetTextAsync()).ToString());
        Assert.Empty(await Analyze(updated));
    }

    [Fact]
    public async Task Token_fix_keeps_generic_binding_and_argument_order()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading;
            class Sample
            {
                static int trace;
                static int Next(int n) => trace = trace * 10 + n;
                static void Send<T>(T value, int priority = 0, CancellationToken @event = default) { }
                static void Send<T>(T value, string priority, CancellationToken @event = default) { }

                public static string Run(CancellationToken @class)
                {
                    Send<int>(priority: Next(1), /* keep evaluation order */ value: Next(2));
                    return trace.ToString();
                }
            }
            """);

        var before = await BoundSend(document);
        var updated = await ApplyOnlyFix(document);
        Assert.Equal(before, await BoundSend(updated));
        Assert.Contains("Send<int>(priority: Next(1), /* keep evaluation order */ value: Next(2), @event: @class)",
            (await updated.GetTextAsync()).ToString());
        Assert.Equal("12", await RunCompiled(document));
        Assert.Equal("12", await RunCompiled(updated));
        Assert.Empty(await Analyze(updated));
    }

    [Fact]
    public async Task Token_fix_escapes_contextual_keywords_in_the_saved_source()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading;
            using System.Threading.Tasks;

            class Sample
            {
                static Task Send(CancellationToken token = default) => Task.CompletedTask;
                async Task Run(CancellationToken @await) => await Send();
            }
            """);

        var updated = await ApplyOnlyFix(document);
        var saved = CreateDocument(workspace, (await updated.GetTextAsync()).ToString());
        var compilation = (await saved.Project.GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(await Analyze(saved));
    }

    [Theory]
    [InlineData("return task.Result;")]
    [InlineData("lock (this) { var value = task.Result; return value; }")]
    [InlineData("var value = task./* rationale */Result; return value;")]
    public async Task Result_fix_is_absent_in_unsupported_contexts(string body)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;
            class Sample
            {
                async Task<int> Run(Task<int> task)
                {
            """ + body + "}}");

        Assert.Empty(await GetFixes(document, Assert.Single(await Analyze(document))));
    }

    [Theory]
    [InlineData("""
        static void Send(long id, CancellationToken ct = default) { }
        static void Send(int id, CancellationToken ct) { }
        """)]
    [InlineData("""
        static void Send(int id, CancellationToken ct = default,
            [System.Runtime.CompilerServices.CallerArgumentExpression("ct")] string? expression = null) { }
        """)]
    public async Task Token_fix_is_absent_when_binding_or_implicit_arguments_are_at_risk(string overloads)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading;
            class Sample
            {
                void Run(CancellationToken token) => Send(1);
            """ + overloads + "}");

        var diagnostic = Assert.Single(await Analyze(document));
        Assert.Equal("AG003", diagnostic.Id);
        Assert.Empty(await GetFixes(document, diagnostic));
    }

    [Fact]
    public async Task Result_fix_does_not_use_a_derived_tasks_hidden_awaiter()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            class CustomTask : Task<int>
            {
                public CustomTask() : base(() => 1) { }
                public new TaskAwaiter<int> GetAwaiter() => Task.FromResult(2).GetAwaiter();
            }

            class Sample
            {
                async Task<int> Run(CustomTask task)
                {
                    var result = task.Result;
                    return result;
                }
            }
            """);

        var diagnostic = Assert.Single(await Analyze(document));
        Assert.Equal("AG001", diagnostic.Id);
        Assert.Empty(await GetFixes(document, diagnostic));
    }

    [Fact]
    public async Task Token_fix_is_absent_inside_a_lambda()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System;
            using System.Threading;

            class Sample
            {
                static void Send(CancellationToken ct = default) { }
                Action<CancellationToken> Create() => token => Send();
            }
            """);

        var diagnostic = Assert.Single(await Analyze(document));
        Assert.Equal("AG003", diagnostic.Id);
        Assert.Empty(await GetFixes(document, diagnostic));
    }

    [Theory]
    [InlineData("Capture(Send())", "Capture(Send(token: ct))")]
    [InlineData("new Captured(Send()).Text", "new Captured(Send(token: ct)).Text")]
    [InlineData("new Captured(0)[Send()]", "new Captured(0)[Send(token: ct)]")]
    [InlineData("Send().CaptureReceiver()", "Send(token: ct).CaptureReceiver()")]
    public async Task Token_fix_preserves_text_captured_by_an_enclosing_call(string original, string rewritten)
    {
        using var workspace = new AdhocWorkspace();
        const string source = """
            using System.Runtime.CompilerServices;
            using System.Threading;

            class Sample
            {
                static int Send(CancellationToken token = default) => 1;
                static string Capture(int value, [CallerArgumentExpression("value")] string text = "") => text;
                public static string Run(CancellationToken ct) => EXPRESSION;
            }

            class Captured(int value, [CallerArgumentExpression("value")] string text = "")
            {
                public string Text => text;

                public string this[int index, [CallerArgumentExpression("index")] string argument = ""]
                    => argument;
            }

            static class Extensions
            {
                public static string CaptureReceiver(this int value,
                    [CallerArgumentExpression("value")] string text = "") => text;
            }
            """;

        var document = CreateDocument(workspace, source.Replace("EXPRESSION", original));
        var unsafeEdit = CreateDocument(workspace, source.Replace("EXPRESSION", rewritten));
        Assert.Equal("Send()", await RunCompiled(document));
        Assert.Equal("Send(token: ct)", await RunCompiled(unsafeEdit));

        var diagnostic = Assert.Single(await Analyze(document));
        Assert.Equal("AG003", diagnostic.Id);
        Assert.Empty(await GetFixes(document, diagnostic));
    }

    [Fact]
    public async Task An_explicit_capture_argument_does_not_block_the_token_fix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Runtime.CompilerServices;
            using System.Threading;

            class Sample
            {
                static int Send(CancellationToken token = default) => 1;
                static string Capture(int value, [CallerArgumentExpression("value")] string text = "") => text;
                public static string Run(CancellationToken ct) => Capture(Send(), text: "explicit");
            }
            """);

        var updated = await ApplyOnlyFix(document);
        Assert.Equal("explicit", await RunCompiled(updated));
        Assert.Empty(await Analyze(updated));
    }

    [Fact]
    public async Task Result_fix_is_absent_when_an_enclosing_call_captures_the_async_lambda()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            class Sample
            {
                static string Capture(Func<Task<int>> read, [CallerArgumentExpression("read")] string text = "")
                    => text;

                string Run(Task<int> task) => Capture(async () =>
                {
                    await Task.Yield();
                    var result = task.Result;
                    return result;
                });
            }
            """);

        var diagnostic = Assert.Single(await Analyze(document));
        Assert.Equal("AG001", diagnostic.Id);
        Assert.Empty(await GetFixes(document, diagnostic));
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public async Task Measures_token_fix_registration_in_a_large_document()
    {
        const int methodCount = 5000;
        var methods = string.Join("\n", Enumerable.Range(0, methodCount)
            .Select(i => $"    static int Method{i}() => {i};"));
        var source = $$"""
            using System.Threading;
            class Sample
            {
            {{methods}}
                static void Send(CancellationToken token = default) { }
                static void Run(CancellationToken ct) => Send();
            }
            """;

        for (var run = 1; run <= 6; run++)
        {
            using var workspace = new AdhocWorkspace();
            var document = CreateDocument(workspace, source);
            var diagnostic = Assert.Single(await Analyze(document));

            var start = Stopwatch.GetTimestamp();
            var actions = await GetFixes(document, diagnostic);
            var elapsed = Stopwatch.GetElapsedTime(start);
            Assert.Single(actions);
            output.WriteLine($"Run {run}: {elapsed.TotalMilliseconds:F1} ms");
        }
    }

    private static async Task<string> RunCompiled(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;

        var loadContext = new AssemblyLoadContext(name: null, isCollectible: true);
        try
        {
            var method = loadContext.LoadFromStream(stream).GetType("Sample")!.GetMethod("Run")!;
            return Assert.IsType<string>(method.Invoke(null, new object[] { CancellationToken.None }));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static async Task AssertDiagnostics(string marked, string id, params string[] messages)
    {
        var spans = new List<TextSpan>();
        var source = Regex.Replace(marked, @"\[\|(.*?)\|\]", match =>
        {
            spans.Add(new TextSpan(match.Index - 4 * spans.Count, match.Groups[1].Length));
            return match.Groups[1].Value;
        });

        using var workspace = new AdhocWorkspace();
        var diagnostics = await Analyze(CreateDocument(workspace, source));
        Assert.Equal(spans, diagnostics.Select(d => d.Location.SourceSpan));
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(id, d.Id);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });
        Assert.Equal(messages, diagnostics.Select(d => d.GetMessage()));
    }

    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

    private static Document CreateDocument(AdhocWorkspace workspace, string source, string file = "Sample.cs",
        string? editorConfig = null)
    {
        var project = workspace.AddProject("Sample", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(References);
        var directory = Path.GetTempPath();
        if (editorConfig is not null)
        {
            project = project.AddAnalyzerConfigDocument(".editorconfig", SourceText.From(editorConfig),
                filePath: Path.Combine(directory, ".editorconfig")).Project;
        }

        return project.AddDocument(file, SourceText.From(source), filePath: Path.Combine(directory, file));
    }

    private static async Task<Diagnostic[]> Analyze(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new AsyncCodeAnalyzer()), document.Project.AnalyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
    }

    private static async Task<List<CodeAction>> GetFixes(Document document, Diagnostic diagnostic)
    {
        var actions = new List<CodeAction>();
        await new AsyncCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), CancellationToken.None));
        return actions;
    }

    private static async Task<Document> ApplyOnlyFix(Document document)
    {
        var action = Assert.Single(await GetFixes(document, Assert.Single(await Analyze(document))));
        var operation = Assert.Single((await action.GetOperationsAsync(CancellationToken.None))
            .OfType<ApplyChangesOperation>());
        var updated = operation.ChangedSolution.GetDocument(document.Id)!;
        var compilation = (await updated.Project.GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return updated;
    }

    private static async Task<string> BoundSend(Document document)
    {
        var root = (await document.GetSyntaxRootAsync())!;
        var call = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .First();
        var model = (await document.GetSemanticModelAsync())!;
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call).Symbol);
        return method.ToDisplayString();
    }
}
