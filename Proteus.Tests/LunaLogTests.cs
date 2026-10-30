using System;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Proteus.Interop;
using Xunit;

namespace Proteus.Tests;

public sealed class LunaLogTests
{
    [Fact]
    public void A_handler_error_reaches_the_plugin_log_with_its_exception()
    {
        var log = Substitute.For<IPluginLog>();
        var boom = new InvalidOperationException("boom");

        new LunaLog(log).LogError(boom, "Exception invoking IPC event {Label}.", "Glamourer.StateFinalized");

        log.Received(1).Error(boom, "[Luna] {Message:l}",
            Arg.Is<object[]>(v => (string)v[0] == "Exception invoking IPC event Glamourer.StateFinalized."));
    }

    [Fact]
    public void Braces_in_a_rendered_message_are_passed_as_a_value_not_a_template()
    {
        var log = Substitute.For<IPluginLog>();

        new LunaLog(log).LogWarning("{Text}", "{0} {not a hole}");

        log.Received(1).Warning(null, "[Luna] {Message:l}", Arg.Is<object[]>(v => (string)v[0] == "{0} {not a hole}"));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "Verbose")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, "Information")]
    [InlineData(LogLevel.Critical, "Error")]
    public void Each_level_lands_on_its_plugin_log_level(LogLevel level, string expected)
    {
        var log = Substitute.For<IPluginLog>();

        new LunaLog(log).Log(level, "m");

        var call = Assert.Single(log.ReceivedCalls());
        Assert.Equal(expected, call.GetMethodInfo().Name);
    }

    [Fact]
    public void None_is_never_logged() => Assert.False(new LunaLog(Substitute.For<IPluginLog>()).IsEnabled(LogLevel.None));
}
