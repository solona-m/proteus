/**
 * Every feature key a report may carry. Anything else is refused, so the endpoint can only ever hold
 * counts of things listed here — never free text, never something a client made up.
 *
 * Kept in sync with UsageStats.WireKey in Proteus/Services/UsageStats.cs; UsageStatsTests reads this
 * file and fails if the two lists differ. Removing a key is safe (old rows keep it); renaming one splits
 * its history in two, so add a new key instead.
 */
export const FEATURES = [
  'composite',
  'second_skin',
  'studio_open',
  'live_brush',
  'mesh_toggle',
  'import_onion',
  'import_pmp',
  'import_ttmp2',
  'import_emissive',
  'import_eye',
  'import_installed',
  'preset_save',
  'preset_apply',
  'design_bind_capture',
  'design_bind_restore',
  'hat_compat',
  'colorset_edit',
  'masks_edit',
  'mod_create',
  'mod_export',
];
