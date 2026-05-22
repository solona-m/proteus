global using IpcOverlayDetail = (
    string ModDirectory, 
    string Name, 
    int Priority, 
    System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? Options
);

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Interop;

public class IpcProvider : IDisposable {
    private const string IpcNamespace = nameof(Proteus);
    private const int MajorVersion = 1;
    private const int MinorVersion = 0;

    private readonly IPluginLog _log;
    private readonly CompositorService _compositor;
    private readonly SidecarDiscoveryService _discovery;

    private readonly ICallGateProvider<List<IpcOverlayDetail>> _getOverlaysProvider;
    private readonly ICallGateProvider<List<IpcOverlayDetail>> _getActiveOverlaysProvider;
    private readonly ICallGateProvider<(int, int)> _apiVersionProvider;
    private readonly ICallGateProvider<object> _recompositeProvider;
    private readonly ICallGateProvider<string, string?, string?, string> _getColorTableProvider;
    private readonly ICallGateProvider<string, string?, string?, string, bool> _setColorTableProvider;
    
    public IpcProvider(IDalamudPluginInterface pluginInterface, CompositorService compositor, SidecarDiscoveryService discovery, IPluginLog log) {
        _compositor = compositor;
        _discovery = discovery;
        _log = log;
        
        _apiVersionProvider = pluginInterface.GetIpcProvider<(int, int)>($"{IpcNamespace}.ApiVersion");
        _apiVersionProvider.RegisterFunc(() => (MajorVersion, MinorVersion));
        
        _getOverlaysProvider = pluginInterface.GetIpcProvider<List<IpcOverlayDetail>>($"{IpcNamespace}.{nameof(GetOverlays)}");
        _getOverlaysProvider.RegisterFunc(GetOverlays);
        
        _getActiveOverlaysProvider = pluginInterface.GetIpcProvider<List<IpcOverlayDetail>>($"{IpcNamespace}.{nameof(GetActiveOverlays)}");
        _getActiveOverlaysProvider.RegisterFunc(GetActiveOverlays);
        
        _recompositeProvider = pluginInterface.GetIpcProvider<object>($"{IpcNamespace}.{nameof(Recomposite)}");
        _recompositeProvider.RegisterAction(Recomposite);
        
        _getColorTableProvider = pluginInterface.GetIpcProvider<string, string?, string?, string>($"{IpcNamespace}.{nameof(GetColorTable)}");
        _getColorTableProvider.RegisterFunc(GetColorTable);
        
        _setColorTableProvider = pluginInterface.GetIpcProvider<string, string?, string?, string, bool>($"{IpcNamespace}.{nameof(SetColorTable)}");
        _setColorTableProvider.RegisterFunc(SetColorTable);
    }
    
    private List<IpcOverlayDetail> GetOverlays() {
        return _compositor.LastDiscovered.Select(overlay => (
            overlay.ModDirectory, 
            overlay.ModName,
            overlay.Priority,
            overlay.Metadata.OptionGroups?.ToDictionary(g => g.PenumbraGroupName, g => g.Options.Select(o => o.Name).ToList())
          )).ToList();
    }
    
    private List<IpcOverlayDetail> GetActiveOverlays() {
        return _compositor.LastDiscovered.Select(overlay => (
            overlay.ModDirectory, 
            overlay.ModName,
            overlay.Priority,
            overlay.Metadata.OptionGroups == null ? null : _discovery.ResolveActiveOverlays(overlay)
                                                                     .GroupBy(g => g.OptionGroup)
                                                                     .ToDictionary(
                                                                         grouping => grouping.Key ?? string.Empty, 
                                                                         grouping => grouping.Select( o => o.Option).ToList()
                                                                         )
          )).ToList();
    }
    
    private void Recomposite() {
        _compositor.TriggerRecomposite("ipc-requested");
    }
    
    private string GetColorTable(string modDirectory, string? group, string? option) {
        try {
            var entry = _compositor.LastDiscovered.FirstOrDefault(s => s.ModDirectory.Equals(modDirectory, StringComparison.InvariantCultureIgnoreCase));
            if (entry == null) return string.Empty;

            if (group != null) {
                if (option == null || entry.Metadata.OptionGroups == null) return string.Empty;
                var overlayOptionGroup = entry.Metadata.OptionGroups?.FirstOrDefault(g => g.PenumbraGroupName.Equals(group, StringComparison.InvariantCultureIgnoreCase));
                var overlayOption = overlayOptionGroup?.Options.FirstOrDefault(o => o.Name.Equals(option, StringComparison.InvariantCultureIgnoreCase));
                if (overlayOption?.ColorTableRows == null) return string.Empty;
                return JsonSerializer.Serialize(overlayOption.ColorTableRows);
            }
            
            if (entry.Metadata.ColorTableRows == null) return string.Empty;
            return JsonSerializer.Serialize(entry.Metadata.ColorTableRows);
        } catch (Exception ex) {
            _log.Error(ex, "Error in IPC GetColorTable");
            return string.Empty;
        }
    }
    
    private bool SetColorTable(string modDirectory, string? group, string? option, string colorTableJson) {
        try {
            var entry = _compositor.LastDiscovered.FirstOrDefault(s => s.ModDirectory.Equals(modDirectory, StringComparison.InvariantCultureIgnoreCase));
            if (entry == null) return false;
            var newColorTable = string.IsNullOrWhiteSpace(colorTableJson) ? null : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(colorTableJson);

            if (group != null) {
                if (option == null) return false;
                if (entry.Metadata.OptionGroups == null) return false;
                var overlayOptionGroup = entry.Metadata.OptionGroups?.FirstOrDefault(g => g.PenumbraGroupName.Equals(group, StringComparison.InvariantCultureIgnoreCase));
                var overlayOption = overlayOptionGroup?.Options.FirstOrDefault(o => o.Name.Equals(option, StringComparison.InvariantCultureIgnoreCase));
                if (overlayOption == null) return false;
                overlayOption.ColorTableRows =  newColorTable;
            } else {
                entry.Metadata.ColorTableRows = newColorTable;
            }

            _discovery.SaveMetadata(entry);
            _compositor.TriggerRecomposite("ipc-colors-change");
            return true;
        } catch (Exception ex) {
            _log.Error(ex, "Error in IPC SetColorTable");
            return false;
        }
    }
    
    public void Dispose() {
        _apiVersionProvider.UnregisterFunc();
        _getOverlaysProvider.UnregisterFunc();
        _getActiveOverlaysProvider.UnregisterFunc();
        _recompositeProvider.UnregisterAction();
        _getColorTableProvider.UnregisterFunc();
        _setColorTableProvider.UnregisterFunc();
    }
}
