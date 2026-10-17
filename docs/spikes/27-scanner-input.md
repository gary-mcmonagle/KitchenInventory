# Spike #27: Scanner input on the Pi

Card: https://trello.com/c/kS5DtItm

## Question

How do we reliably read barcodes from two **Tera 5100** wireless scanners on the headless Pi 3, and tell which scanner each scan came from (check-in or check-out)?

## Summary

- Each **dongle** shows up as a plain **USB HID keyboard**. A scan is typed as key presses and ends with **Enter**.
- We read the dongles headlessly from `/dev/input` with the **Python standard library only** (no `evdev` package). Each one is grabbed exclusively (`EVIOCGRAB`), so scans never reach the console.
- **The two scanners are told apart by the USB port their dongle is in** (the `/dev/input/by-path` link). This needs no scanner configuration and was proven with both scanners. `/dev/input/by-id` **can't** be used: both dongles are identical and share a single by-id link.
- **One scanner setting is needed:** scan the manual's **UK keyboard layout** barcode on each scanner. It shipped set to French (AZERTY), which holds Shift for every digit. The reader copes with that anyway, but UK gives clean key codes.
- EAN-13, EAN-8 and UPC-A read with no stray characters. Sleep, and unplug/replug, both recover without a restart.
- **No different second scanner is needed.**
- Prefix/suffix configuration isn't needed. It's kept as a fallback (see [Recommendation](#recommendation)).
- **Not tested:** walking out of range.

## Setup

- Pi 3 (`cupboard-pi.local`), Raspberry Pi OS Lite 13 (trixie), armv7l, Python 3.13. User `gary` is in the `input` group, so no root is needed.
- Two Tera 5100 scanners, each paired to its own dongle, in USB HID keyboard mode (the default).
- The reader: [`27-scanner-read.py`](27-scanner-read.py), run on the Pi as `python3 27-scanner-read.py [--seconds N] [--raw]`.

## Findings

### 1. How it presents

A USB HID keyboard. Both dongles have the same USB ID, the same name, and no serial number (`Uniq=` is empty):

```
$ lsusb | grep 0483
Bus 001 Device 005: ID 0483:0240 STMicroelectronics USB Device
Bus 001 Device 006: ID 0483:0240 STMicroelectronics USB Device

$ grep -B1 -A4 "USB Adapter" /proc/bus/input/devices
N: Name="USB Adapter USB Device"
P: Phys=usb-3f980000.usb-1.5/input0
U: Uniq=
H: Handlers=sysrq kbd leds event2
N: Name="USB Adapter USB Device"
P: Phys=usb-3f980000.usb-1.2/input0
U: Uniq=
H: Handlers=sysrq kbd leds event3
```

It isn't a serial device, and nothing else needs to be installed.

### 2. Headless reading

- The reader opens each `/dev/input/by-path/*-event-kbd` link that's on USB, and reads `struct input_event` records directly (`struct.Struct("LLHHi")`, 16 bytes on the 32-bit Pi OS).
- It calls `ioctl(EVIOCGRAB)` on each one, so the key presses go only to the reader. They don't reach the login console or anything else.
- It turns key codes into characters, and a scan ends at **Enter** (key code 28).
- It ignores the `usbv2` duplicate links udev also creates, so each dongle is opened once.
- `select()` waits on all dongles at once. A scan takes 32–52 ms to arrive (about 4 ms per digit).

**Keyboard layout matters.** Out of the box, the scanners were set to a **French (AZERTY)** layout. On AZERTY the digits are on the shifted top row, so the scanner held Shift for every digit and a US-layout decode gave symbols. Raw events for the first digit of Bisto's `5000354922831`:

```
raw ... code=42 value=1     ← Left Shift down
raw ... code=6 value=1      ← the "5" key
raw ... code=42 value=0     ← Shift up
```

Decoded on a US layout, the first run's scans came out as `%)))#%$(@@*#!` (for 5000354922831). Two fixes, both in place:

- **On the scanner:** Gary scanned the manual's **UK** keyboard barcode (with the interface kept on **USB HID Keyboard**). After that, digits arrive with no Shift.
- **In the reader:** top-row digit keys are decoded as digits whether or not Shift is held. Barcodes are digits, so this is always right, and a scanner reset to its factory settings still reads correctly.

### 3. Telling scanners apart

The two dongles appear as **separate input devices** (event2, event3). They have **one shared by-id link**, but a **unique by-path link per USB port**:

```
$ ls -l /dev/input/by-id/
usb-USB_Adapter_USB_Device-event-kbd -> ../event3            ← only one: the second dongle took it

$ ls -l /dev/input/by-path/
platform-3f980000.usb-usb-0:1.2:1.0-event-kbd -> ../event3
platform-3f980000.usb-usb-0:1.5:1.0-event-kbd -> ../event2
platform-3f980000.usb-usbv2-0:1.2:1.0-event-kbd -> ../event3   ← duplicates, ignored
platform-3f980000.usb-usbv2-0:1.5:1.0-event-kbd -> ../event2
```

Gary scanned Bisto on scanner 1 and naan on scanner 2. Each scan came from its own dongle's port:

```
+ scanner platform-3f980000.usb-usb-0:1.2:1.0 (/dev/input/event3)
platform-3f980000.usb-usb-0:1.5:1.0 5000354922831   [13 chars, 52 ms]
platform-3f980000.usb-usb-0:1.2:1.0 20055363   [8 chars, 32 ms]
```

The port paths, looking at the Pi's USB ports:

| Scanner | Physical port | Scanner port (by-path) |
|---|---|---|
| Scanner 1 | Bottom right | `platform-3f980000.usb-usb-0:1.5:1.0` |
| Scanner 2 | Top left | `platform-3f980000.usb-usb-0:1.2:1.0` |

The by-path link is set by the physical port. It came back the same after an unplug and replug (below), and doesn't depend on the order the dongles were plugged in.

**Prefix/suffix** (the other option on the card) wasn't needed. It would let a scan identify itself even if the dongles were swapped, but it's lost if a scanner is reset to factory settings, and it needs the reader to strip the prefix. See the recommendation.

### 4. Formats

All read cleanly, with no stray characters (first run, before the UK layout change, decoded from the raw key codes; then again after it):

| Pack | Format | Read |
|---|---|---|
| Bisto gravy | EAN-13 | `5000354922831` |
| Freshona chickpeas (scanned twice quickly) | EAN-13 | `4056489003703`, `4056489003703` |
| Naan | EAN-8 | `20055363` |
| Chia seeds | EAN-8 | `20719395` |
| A 12-digit pack from the first run | UPC-A | `049733830119` |

Two quick scans of the chickpeas arrived as two separate lines. Nothing was merged or dropped.

### 5. Robustness

| Case | Result |
|---|---|
| **Unplug and replug the dongle** | The reader logs `- scanner … gone`, then `+ scanner …` with the **same by-path** within 2 s of plugging back in. The next scan (Bisto) read normally. No restart needed. |
| **Second dongle plugged in while running** | Picked up automatically (the 2 s rescan). |
| **Scanner sleeps / turns itself off** | Scanner 2 switched itself off after being idle. Gary turned it back on and scanned the naan, and it read normally. The dongle stays connected while the scanner is off, so the Pi sees nothing happen. The reader needs no special handling. |
| **Out of range** | **Not tested.** If a scanner is out of range, the scan never reaches the dongle, so the Pi can't know about it. Whether the scanner beeps an error, or stores the scan and sends it later, depends on the scanner's mode (see follow-ups). |

### 6. The second scanner

Both scanners were here, so question 3 was answered by testing rather than from the manual.

## Recommendation

1. **Read the dongles from `/dev/input/by-path`, with an exclusive grab, using the standard library.** The reader in this spike is the basis for the Pi client's scanner input.
2. **Identify scanners by scanner port.** The Pi client's config maps each by-path name to a device, e.g. `platform-3f980000.usb-usb-0:1.5:1.0=scanner-in`. Ignore unknown ports: log a warning, and don't send their scans.
3. **Label the dongles and ports** ("IN" / "OUT") so they don't get swapped. Swapped dongles would turn check-ins into check-outs.
4. **Decode digit keys regardless of Shift**, and keep the scanners on the UK layout.
5. **Don't configure prefixes now.** Add them only if swapped dongles become a real problem.

### Notes for the Pi client deployment (#33)

- **No `evdev` package is needed.** The reader is the standard library only, so nothing needs compiling on the Pi (no `python3-dev` / `gcc`).
- The service must run as a user in the **`input` group**. `gary` already is, so the service can run as `gary`.

### Scanner configuration (both scanners)

1. Interface: **USB HID Keyboard** (the default; leave it).
2. Keyboard layout: scan the manual's **UK** barcode.

No other config barcodes are needed.

### Is a different second scanner needed?

**No.** Two identical Tera 5100s work, told apart by USB port.

## Suggested follow-up cards

Not created; for Gary to decide.

- **[Feature] Pi reads scans and sends scan events.** Build this spike's reader into the Pi client:
  - A scanner-port → device mapping in `pi.env` (replacing the planned `SCANNERS` list).
  - Digit decoding.
  - Validate barcodes with `is_valid_barcode`, then send them as scan events.
  - Tests for the key decoding.
- **[Spike] Out-of-range behaviour.** Find out what a Tera 5100 does with a scan made out of range. Does it beep an error, or store the scan and send it later (the manual's "inventory"/"store" mode)? If it sends later, the scan's timestamp is the time it arrived, not the time it was made.
- **[Task] Label dongles and USB ports** (Gary, physical). Put "IN" / "OUT" stickers on each dongle and its port.
