using Microsoft.JSInterop;

namespace ActionLens.Ui.Services;

/// <summary>
/// The seam between the UI components and the host's JavaScript interop module.
/// </summary>
/// <remarks>
/// The CodeMirror bundle is a host asset (<c>ActionLens.App/wwwroot/js/dist</c>) and cannot load under
/// bUnit, so the components never reference the concrete <c>EditorModule</c> — they ask for this interface
/// and degrade honestly when <see cref="GetAsync"/> returns null. The host registers its real module; a
/// test registers a stub that always returns null, which is exactly the documented textarea-fallback path.
/// </remarks>
public interface IEditorModule
{
    /// <summary>Why the module could not be loaded, when it could not. Shown in the status strip.</summary>
    string? FailureReason { get; }

    /// <summary>True once the module has been imported successfully.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// The module reference, or null when it is unavailable. Concurrent callers share one import.
    /// </summary>
    Task<IJSObjectReference?> GetAsync();
}
