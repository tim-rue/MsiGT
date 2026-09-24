# Graphics Switcher

One-click toggle between **Hybrid (MSHybrid)** and **Integrated** graphics on MSI laptops with the
"new GPU switch" (tested on a Creator Z16HX Studio B13VGTO), without opening MSI Center.

## Use

Run `publish\GraphicsSwitcher.exe` (pin it to Start/taskbar or make a desktop shortcut). It asks for
admin rights, shows the current mode and offers to switch and restart now, or switch on the next restart.
If a switch is already pending, running it again lets you restart or stay in the current mode.

Command line (no dialogs): `--hybrid`, `--integrated`, `--status-file <path>`.

## How it works

The same mechanism MSI Center uses (reverse-engineered from `OmApSvcBroker.exe` in the MSI NBFoundation Service):

- Byte 5 of the UEFI variable `MsiDCVarData` `{DD96BAAF-145E-4F56-B1CF-193256298E99}`:
  bits 0-1 = mode for next boot, bits 2-3 = current mode (0 Hybrid, 1 Discrete, 2 Integrated),
  bit 4 = switch supported, bit 5 = Integrated supported, bit 6 = Discrete *not* supported.
- A switch is requested by writing `GPUswitchCH` (DWORD) under
  `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\GeneralSetting`; the MSI service
  applies it to the UEFI variable. The firmware switches on the next reboot.
- If the MSI service doesn't apply the request within 4 seconds (e.g. MSI Center stuck on "Waiting for SDK
  initialization"), the app writes the same 2 bits to the UEFI variable itself, keeping all other bytes and
  attributes unchanged.
- Storing the mode is not enough: the switch must also be armed in the EC, as MSI Center's
  `GraphicsSwithc` command does. Read `MSI_ACPI.Get_AP(0)`, set the low two bits of byte 1 to `01`, and write it
  to EC register `0xD1` via `MSI_ACPI.Set_Data`. The firmware acknowledges by setting `Get_AP(0)` byte 2 to `2`,
  and the switch then happens on the next reboot.

Build: `dotnet publish -c Release -o publish` (.NET 9 desktop runtime).
