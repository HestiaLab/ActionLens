using ActionLens.App;
using ActionLens.App.Services;
using ActionLens.Ui.Services;
using Atlas.Blazor.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Atlas workspace services (the host component, the factory, and the in-memory layout stores).
builder.Services.AddAtlasWorkspace();

// ActionLens shell state. There is deliberately no HttpClient: the app reads nothing over the network at
// runtime, which is the promise ADR 0003 makes and which a later test phase checks.
builder.Services.AddScoped<AnalysisSession>();
builder.Services.AddScoped<EditorSurfaceMonitor>();
// The Ui components inject IEditorModule; the CodeMirror bundle is a host asset, so the host registers
// the only implementation. bUnit substitutes a stub — the bundle cannot load outside a browser.
builder.Services.AddScoped<IEditorModule, EditorModule>();

await builder.Build().RunAsync();
