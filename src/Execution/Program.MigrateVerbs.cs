using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A byte flow's read, <c>.load(max:</c>, or the routine that does it (<c>FileSource.load(max:</c>).</summary>
    private static readonly Regex FlowLoad = new(pattern: @"\.load(?=\s*\(\s*max\s*:)", options: RegexOptions.Compiled);

    /// <summary>A byte flow's write, <c>.store(data:</c>, or the routine that does it.</summary>
    private static readonly Regex FlowStore = new(pattern: @"\.store(?=\s*\(\s*data\s*:)", options: RegexOptions.Compiled);

    /// <summary>A channel's send, <c>.send(item:</c>, and <c>.send_if_room(</c>.</summary>
    private static readonly Regex ChannelSend = new(pattern: @"\.send(?<if_room>_if_room)?(?=\s*\()",
        options: RegexOptions.Compiled);

    /// <summary>The sending end of a channel, <c>Sender</c>, where it names the type.</summary>
    private static readonly Regex SenderType = new(pattern: @"(?<![\w.`$])Sender\b", options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step verbs</c>: the verbs say what moves. A byte flow (a <c>Source</c> or <c>Sink</c>: <c>FileSource</c>,
    /// <c>FileSink</c>, <c>stdin()</c>, <c>stdout()</c>) is read with <c>pull(max:)</c> and written with
    /// <c>push(data:)</c>, so <c>.load(max:</c> and <c>.store(data:</c> are renamed (a place's <c>load()</c> /
    /// <c>store(desired:)</c> takes other arguments and stays). A channel moves one message at a time, so
    /// <c>.send(</c> is <c>.transmit(</c>, <c>.send_if_room(</c> is <c>.transmit_if_room(</c> and the type
    /// <c>Sender</c> is <c>Transmitter</c>. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteVerbs(string file, string source, Language language, int lineOffset)
    {
        string text = ReplaceInCode(text: source, pattern: FlowLoad, replacement: _ => ".pull");
        text = ReplaceInCode(text: text, pattern: FlowStore, replacement: _ => ".push");
        text = ReplaceInCode(text: text, pattern: ChannelSend,
            replacement: m => m.Groups[groupname: "if_room"].Success ? ".transmit_if_room" : ".transmit");
        return ReplaceInCode(text: text, pattern: SenderType, replacement: _ => "Transmitter");
    }
}
