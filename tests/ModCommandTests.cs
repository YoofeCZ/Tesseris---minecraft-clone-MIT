using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModCommandTests
{
    [Fact]
    public void Command_parser_preserves_quoted_empty_and_escaped_arguments()
    {
        var platform = new ModClientPlatform();
        ModCommandInvocation? captured = null;
        platform.ForMod("test").Commands.Register(
            new ResourceId("test:echo"),
            "/test:echo <values...>",
            0,
            new CommandHandler((invocation, output) =>
            {
                captured = invocation;
                output.Reply(string.Join("|", invocation.Arguments));
                return ModActionResult.Handled;
            }));
        platform.Freeze();
        var output = new RecordingOutput();

        ModCommandExecutionResult result = platform.Commands.Execute(
            "/test:echo plain \"two words\" \"\" escaped\\ value",
            output,
            playerId: 42,
            isRemote: true);

        Assert.Equal(ModCommandStatus.Executed, result.Status);
        Assert.Equal(["plain", "two words", "", "escaped value"], captured!.Arguments);
        Assert.Equal(42UL, captured.PlayerId);
        Assert.True(captured.IsRemote);
        Assert.Equal(["plain|two words||escaped value"], output.Replies);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)captured.Arguments).Add("mutation"));
    }

    [Fact]
    public void Autocomplete_and_registration_snapshots_use_priority_then_id()
    {
        var platform = new ModClientPlatform();
        IModCommandRegistry commands = platform.ForMod("test").Commands;
        commands.Register(new ResourceId("test:zeta"), "/test:zeta", -1, PassHandler());
        commands.Register(new ResourceId("test:alpha"), "/test:alpha", 0, PassHandler());
        commands.Register(new ResourceId("test:beta"), "/test:beta", 0, PassHandler());
        platform.Freeze();

        Assert.Equal(
            ["test:zeta", "test:alpha", "test:beta"],
            platform.Commands.Registrations.Select(value => value.Id.Value));
        Assert.Equal(
            ["/test:zeta", "/test:alpha", "/test:beta"],
            platform.Commands.Complete("/test:"));
    }

    [Fact]
    public void Parse_and_unknown_command_diagnostics_are_stable()
    {
        var platform = new ModClientPlatform();
        platform.ForMod("test").Commands.Register(
            new ResourceId("test:known"), "/test:known", 0, PassHandler());
        platform.Freeze();

        var parseOutput = new RecordingOutput();
        ModCommandExecutionResult parse = platform.Commands.Execute(
            "/test:known \"unfinished", parseOutput);
        Assert.Equal(ModCommandStatus.ParseError, parse.Status);
        Assert.Contains("not terminated", Assert.Single(parseOutput.Errors), StringComparison.Ordinal);

        var unknownOutput = new RecordingOutput();
        ModCommandExecutionResult unknown = platform.Commands.Execute("/test:kno", unknownOutput);
        Assert.Equal(ModCommandStatus.UnknownCommand, unknown.Status);
        Assert.Contains("/test:known", Assert.Single(unknownOutput.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_registration_and_failures_are_owner_attributed()
    {
        var platform = new ModClientPlatform();
        IModCommandRegistry commands = platform.ForMod("broken").Commands;
        Assert.Throws<InvalidOperationException>(() => commands.Register(
            new ResourceId("other:bad"), "/other:bad", 0, PassHandler()));
        commands.Register(
            new ResourceId("broken:explode"),
            "/broken:explode",
            0,
            new CommandHandler((_, _) => throw new ApplicationException("boom")));
        platform.Freeze();
        Assert.Throws<InvalidOperationException>(() => commands.Register(
            new ResourceId("broken:late"), "/broken:late", 0, PassHandler()));

        ModCommandCallbackException exception = Assert.Throws<ModCommandCallbackException>(() =>
            platform.Commands.Execute("/broken:explode", new RecordingOutput()));
        Assert.Equal("broken", exception.ModId);
        Assert.Equal(new ResourceId("broken:explode"), exception.CommandId);
        Assert.IsType<ApplicationException>(exception.InnerException);
    }

    private static IModCommandHandler PassHandler() =>
        new CommandHandler((_, _) => ModActionResult.Pass);

    private sealed class CommandHandler(
        Func<ModCommandInvocation, IModCommandOutput, ModActionResult> callback) : IModCommandHandler
    {
        public ModActionResult Execute(ModCommandInvocation invocation, IModCommandOutput output) =>
            callback(invocation, output);
    }

    private sealed class RecordingOutput : IModCommandOutput
    {
        public List<string> Replies { get; } = [];
        public List<string> Errors { get; } = [];
        public void Reply(string text) => Replies.Add(text);
        public void Error(string text) => Errors.Add(text);
    }
}
