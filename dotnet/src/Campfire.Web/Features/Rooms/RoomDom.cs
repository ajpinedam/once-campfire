namespace Campfire.Web.Features.Rooms;

/// <summary>
/// Stimulus action lists for the room page (Rails' MessagesHelper / RoomsHelper / RichTextHelper),
/// kept in one place so the templates and the controllers they wire up can't drift.
/// </summary>
public static class RoomDom
{
    private const string DropTargetActions = "dragenter->drop-target#dragenter dragover->drop-target#dragover drop->drop-target#drop";

    /// <summary>submitByKeyboard runs in the capture phase so it can submit on Enter before the editor turns it into a newline.</summary>
    public const string RichTextActions = "lexxy:change->typing-notifications#start keydown->composer#submitByKeyboard:capture";

    public const string MessageAreaActions =
        "turbo:before-stream-render@document->messages#beforeStreamRender keydown.up@document->messages#editMyLastMessage " +
        DropTargetActions + " visibilitychange@document->presence#visibilityChanged";

    public const string MessagesActions =
        "turbo:before-stream-render@document->maintain-scroll#beforeStreamRender " +
        "visibilitychange@document->refresh-room#visibilityChanged online@window->refresh-room#online";

    public const string ComposerActions =
        DropTargetActions + " drop-target:drop@window->composer#dropFiles " +
        "lexxy:file-accept->composer#preventAttachment refresh-room:online@window->composer#online " +
        "typing-notifications#stop paste->composer#pasteFiles turbo:submit-end->composer#submitEnd refresh-room:offline@window->composer#offline";

    public const string ComposerTextActions = RichTextActions + " lexxy:change->composer#saveDraft lexxy:insert-link->unfurl#unfurl";
}
