# Proteus Troubleshooting

This guide helps you collect the information needed to report a bug so it can be diagnosed and fixed quickly.

## Step 0 — First Things
1. do you have the proteus plugin installed and enabled?
2. do you have a proteus enabled mod like my stocking mod?
3. Is the "Protus" mod enabled in penumbra and at a higher priority than your other mods?
4. Is the stockings or other mod enabled?
5. Type /proteus in the chat. Did it discover your mods?

---

## Step 1 — Uninstall the Non-Functioning Mod

Before doing anything else, uninstall the mod from Penumbra so the installation process is clean during the trace.

1. In game, type `/xllog` in the chat.
2. Set the logging level to **Verbose+** at the top left.
3. Click the magnifying glass and add new filters for **Penumbra** and **Proteus** (select them from the dropdown and click the plus).
4. Clear the log with the trash can button.

---

## Step 2 — Install the Mod

1. Install the mod and make sure it is enabled.
2. Copy paste the /xllog window results to a text file and dm it to me on discord (Solona)

---

## Step 3 — Missing Files
If you see errors about missing files in the log, please get this additional log

1. Go to: https://learn.microsoft.com/en-us/sysinternals/downloads/procmon
2. Click **Download Process Monitor** and extract the ZIP anywhere convenient.
3. Run **Procmon64.exe** as Administrator (right-click → Run as administrator).
4. Accept the EULA when prompted. The ProcMon window will open and immediately start capturing.
5. **Pause the capture immediately** by pressing `Ctrl+E` or clicking the magnifying glass icon in the toolbar. You don't want to record anything yet.
6. Clear the existing log: press `Ctrl+X` or go to **Edit → Clear Display**.
7. Add a filter for your Penumbra directory — click the filter icon at the top, filter by path, and click **Add**.
8. Uninstall the mod from Penumbra.
9. **Start capturing:** press `Ctrl+E` (the magnifying glass icon should appear active/lit).
10. Install the mod in Penumbra now — do the full install exactly as you normally would.
11. Once the install is complete, **pause the capture** immediately by pressing `Ctrl+E` again.
12. In Process Monitor, go to **File → Save...**
13. Set the file type to **Native Process Monitor Format (.pml)**.
14. Make sure **All Events** is checked.
15. Save the file as `install.pml` somewhere easy to find.
16. Send the file to **Solona** on Discord.
