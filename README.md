# MSI GPU Tools (MsiGT)

Utilities for MSI laptop GPUs:

- Switch between **Hybrid (MSHybrid)** and **Integrated** graphics without opening MSI Center.
- Free up the discrete GPU in Hybrid mode by closing the processes that keep it awake, so it can power down.

> [!WARNING]
> This is an unofficial tool, based on reverse engineering. It changes an MSI UEFI variable and writes to the embedded
> controller (EC), in the same way MSI Center does. It has been tested on one laptop only. Use at your own risk.

## Usage

Run `MsiGT.exe` and accept the admin prompt. The window has two tabs.

### Graphics mode

1. The tab shows the current mode and offers **Switch and restart now** or **Switch on next restart**.
2. The switch happens during the restart.

If a switch is already pending, the tab lets you restart now or cancel the switch.
If any step fails, the app shows an error and does **not** restart. A half-applied switch is rolled back.

### Free up GPU

In Hybrid mode the discrete GPU should power down when nothing uses it, but apps that once touched it keep it awake.
This tab appears in Hybrid mode (or when the mode can't be read). It lists these processes, refreshed every
2 seconds, and shows their count in the tab title. **Close processes** closes them, with a progress bar, so the GPU
can power down:

| Process | What happens |
| --- | --- |
| Helper process of an app (Chrome, Edge WebView2, VS Code, Teams… GPU/utility processes) | Ended. The app recreates it, now on the power-saving GPU. |
| App | Asked to close the way Windows does at sign-out, so it can save its state. Ended after a timeout (5 s by default). An app that refuses, e.g. because of unsaved work, is left open. |
| Windows Explorer | Exited with its own *Exit Explorer* command and started again. |
| Start, Search, touch keyboard and other shell hosts | Ended. Windows restarts them. |
| Services, other accounts, core Windows processes, MSI Center's service | Left alone. |

Right-click a process to mark it as **restart after closing** or **never close**. **Settings…** edits both lists, whether
Windows components are restarted, and the timeout. Settings are stored in `%APPDATA%\MsiGT\settings.json`.

A display connected to the discrete GPU keeps it on; the tab warns about that.

### Command line

Options (no window):

| Option | Effect |
| --- | --- |
| `--hybrid` | Request Hybrid mode for the next restart |
| `--integrated` | Request Integrated mode for the next restart |
| `--status-file <path>` | Write the current switch state to a file (read-only) |

## Requirements

- Windows 10/11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- MSI Center installed, including the *MSI NBFoundation Service*. It provides the `MSI_ACPI` WMI interface.
- Administrator rights

## Compatibility

| Status | Models |
| --- | --- |
| ✅ Tested | MSI Creator Z16HX Studio (B13VGTO) |
| ❔ Likely | Recent MSI laptops (roughly 2022 or newer; Intel 12th–14th gen or Core Ultra) where MSI Center's GPU switch offers an **Integrated graphics** option. A MUX chip does not appear to be needed: the tested laptop does not support Discrete mode. |
| ❌ Unlikely | Older models (about 2021 and earlier) that use MSI's older WMI v1 interface or older GPU switch; models whose GPU switch only offers MSHybrid/Discrete; laptops without a dedicated GPU |

The "likely" and "unlikely" rows are **estimates** from how MSI Center's code tells old and new hardware apart.
They are not based on testing. Specific uncertainties:

- **EC arming step:** the register (`0xD1`) comes from MSI Center's generic code path, so it is probably the same on all
  models that use the newer WMI v2 interface. It has only been verified on the tested model.
- **AMD models:** unknown. One of MSI Center's older support checks requires an Intel CPU of 10th gen or newer.
  The newer mechanism has no obvious CPU check.
- **MSI Center S and other MSI app variants:** the app may not reach the MSI service through the registry. It then
  writes the UEFI variable directly, but this combination is untested.

**Check your laptop (read-only):** run the following as administrator.

```
MsiGT.exe --status-file status.txt
```

The laptop is very likely compatible if `status.txt` shows `Switch supported: True, Integrated supported: True` and
contains a `Get_AP(0)` line. If the file is not created, an error dialog names the missing piece. The app also refuses
to switch on firmware that does not report support.

### Help improve this list

Anyone is welcome to test the app on their MSI laptop and report back by opening an issue, whether it worked or not.
Please include:

- the exact laptop model (for example *Creator Z16HX Studio B13VGTO*)
- the contents of `status.txt` from the check above
- whether switching worked in each direction, and any error message

Each report makes the compatibility table more reliable.

## Building

```
dotnet publish -c Release -o publish
```

The output is `publish\MsiGT.exe`, a single file that depends on the .NET 10 runtime.

## Releasing

Push a version tag and GitHub Actions builds the app and publishes a release with `MsiGT.exe` and a zip attached:

```
git tag v1.2.0
git push origin v1.2.0
```

The tag sets the version stamped into the exe. Tags with a suffix, like `v1.2.0-beta.1`, are marked as pre-releases.

## How it works

### Switching graphics mode

The app follows the steps MSI Center takes, reverse-engineered from the MSI NBFoundation Service (`OmApSvcBroker.exe`)
and MSI Center's `API_NB_Base Module.dll`:

1. **Store the requested mode.** Byte 5 of the UEFI variable `MsiDCVarData` `{DD96BAAF-145E-4F56-B1CF-193256298E99}` holds:

   | Bits | Meaning |
   | --- | --- |
   | 0–1 | Mode for the next boot (0 = Hybrid, 1 = Discrete, 2 = Integrated) |
   | 2–3 | Current mode |
   | 4 | GPU switch supported |
   | 5 | Integrated mode supported |
   | 6 | Discrete mode *not* supported |

   The app requests a mode by writing `GPUswitchCH` (DWORD) under
   `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\GeneralSetting`. The MSI service watches this value
   and updates the UEFI variable. The service stops watching after sleep and resume, and can also hang (for example
   when MSI Center is stuck at "Waiting for SDK initialization"). So if the variable has not changed after 4 seconds,
   the app writes the same 2 bits itself and leaves the rest of the variable unchanged.

2. **Arm the switch in the EC.** Storing the mode is not enough: the firmware ignores it unless the switch is armed.
   The app reads `MSI_ACPI.Get_AP(0)`, sets the low two bits of byte 1 to `01`, and writes the result to EC register
   `0xD1` with `MSI_ACPI.Set_Data`. The firmware acknowledges by setting byte 2 of `Get_AP(0)` to `2`.

3. **Restart.** The firmware switches modes during boot.

### Freeing the discrete GPU

- **Finding the GPU:** the adapter the Windows graphics kernel flags as *hybrid discrete* (`D3DKMT_ADAPTERTYPE`),
  the same one Windows offers as "High performance". This works with any GPU vendor.
- **Finding its users:** the `GPU Process Memory` performance counters, which Task Manager also uses. They list
  the same processes as `nvidia-smi`, but reading them doesn't touch the GPU. Polling `nvidia-smi` can wake it through NVML, which
  would keep it on. The tab also reads the GPU's power state (`DEVPKEY_Device_PowerData`) and checks active displays
  (`QueryDisplayConfig`).
- **Restarting apps:** the command line and user token are captured before an app is closed, and the app is started
  again with both, so it isn't restarted elevated. Store apps are started again through the shell
  (`shell:AppsFolder\<AUMID>`), since their executables can't be launched directly.
- **Not breaking things:** apps whose helper processes restart on the discrete GPU are reported rather than ended
  again. Ending a Chromium GPU process repeatedly makes Chromium turn off hardware acceleration.

## License

[MIT](LICENSE)
