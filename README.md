# Graphics Switcher

One-click switching between **Hybrid (MSHybrid)** and **Integrated** graphics on MSI laptops, without opening MSI Center.

> [!WARNING]
> This is an unofficial tool, based on reverse engineering. It changes an MSI UEFI variable and writes to the embedded
> controller (EC), in the same way MSI Center does. It has been tested on one laptop only. Use at your own risk.

## Usage

1. Run `GraphicsSwitcher.exe` and accept the admin prompt.
2. The app shows the current mode and offers **Switch and restart now** or **Switch on next restart**.
3. The switch happens during the restart.

If a switch is already pending, running the app again lets you restart now or cancel the switch.
If any step fails, the app shows an error and does **not** restart. A half-applied switch is rolled back.

Command line options (no dialogs):

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
GraphicsSwitcher.exe --status-file status.txt
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

The output is `publish\GraphicsSwitcher.exe`, a single file that depends on the .NET 10 runtime.

## How it works

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

## License

[MIT](LICENSE)
