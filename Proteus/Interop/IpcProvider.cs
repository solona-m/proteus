global using IpcOverlayDetail = (string ModDirectory, string Name, int Priority);

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
    private readonly ICallGateProvider<(int, int)> _apiVersionProvider;
    private readonly ICallGateProvider<object> _recompositeProvider;
    private readonly ICallGateProvider<string, string> _getColorTableProvider;
    private readonly ICallGateProvider<string, string, bool> _setColorTableProvider;
    
    public IpcProvider(IDalamudPluginInterface pluginInterface, CompositorService compositor, SidecarDiscoveryService discovery, IPluginLog log) {
        _compositor = compositor;
        _discovery = discovery;
        _log = log;
        
        _apiVersionProvider = pluginInterface.GetIpcProvider<(int, int)>($"{IpcNamespace}.ApiVersion");
        _apiVersionProvider.RegisterFunc(() => (MajorVersion, MinorVersion));
        
        _getOverlaysProvider = pluginInterface.GetIpcProvider<List<IpcOverlayDetail>>($"{IpcNamespace}.{nameof(GetOverlays)}");
        _getOverlaysProvider.RegisterFunc(GetOverlays);
        
        _recompositeProvider = pluginInterface.GetIpcProvider<object>($"{IpcNamespace}.{nameof(Recomposite)}");
        _recompositeProvider.RegisterAction(Recomposite);
        
        _getColorTableProvider = pluginInterface.GetIpcProvider<string, string>($"{IpcNamespace}.{nameof(GetColorTable)}");
        _getColorTableProvider.RegisterFunc(GetColorTable);
        
        _setColorTableProvider = pluginInterface.GetIpcProvider<string, string, bool>($"{IpcNamespace}.{nameof(SetColorTable)}");
        _setColorTableProvider.RegisterFunc(SetColorTable);
    }
    
    private List<IpcOverlayDetail> GetOverlays() {
        return _compositor.LastDiscovered.Select(overlay => (overlay.ModDirectory, overlay.ModName, overlay.Priority)).ToList();
    }

    public void Dispose() {
        _apiVersionProvider.UnregisterFunc();
        _getOverlaysProvider.UnregisterFunc();
        _recompositeProvider.UnregisterAction();
        _getColorTableProvider.UnregisterFunc();
        _setColorTableProvider.UnregisterFunc();
    }
    
    private void Recomposite() {
        _compositor.TriggerRecomposite("ipc-requested");
    }
    
    private string GetColorTable(string modDirectory) {
        try {
            var entry = _compositor.LastDiscovered.FirstOrDefault(s => s.ModDirectory.Equals(modDirectory, StringComparison.InvariantCultureIgnoreCase));
            if (entry == null) return string.Empty;
            var colorTable = _discovery.GetMergedColorRows(entry);
            return JsonSerializer.Serialize(colorTable);
        } catch (Exception ex) {
            _log.Error(ex, "Error in IPC GetColorTable");
            return string.Empty;
        }
    }
    
    private bool SetColorTable(string modDirectory, string colorTableJson) {
        try {
            var entry = _compositor.LastDiscovered.FirstOrDefault(s => s.ModDirectory.Equals(modDirectory, StringComparison.InvariantCultureIgnoreCase));
            if (entry == null) return false;
            var newColorTable = JsonSerializer.Deserialize<List<ColorTableRowPreset>>(colorTableJson);
            if (newColorTable == null) return false;
            var colorTable = _discovery.GetEditableColorRows(entry);
            colorTable.Clear();
            colorTable.AddRange(newColorTable);
            _discovery.SaveMetadata(entry);
            _compositor.TriggerRecomposite("ipc-colors-change");
            return true;
        } catch (Exception ex) {
            _log.Error(ex, "Error in IPC SetColorTable");
            return false;
        }
    }
}
