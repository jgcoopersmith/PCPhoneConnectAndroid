# PC Phone Connect

Wirelessly mirror and control a real Android phone from a Windows PC over your
local Wi-Fi network — no root, no ADB cable, no cloud.

The phone runs a small agent that captures its screen (via `MediaProjection`) and
streams it as JPEG frames over TCP. A .NET WPF desktop app shows that live screen
inside a phone-shaped window and drives the device with your mouse and keyboard —
taps, swipes, long-presses, and the Back / Home / Recents / Notifications actions —
by injecting gestures through an Android `AccessibilityService`.

```
┌───────────────┐        JPEG frames  (phone → PC)       ┌───────────────┐
│  Android app  │  ───────────────────────────────────▶ │   WPF app      │
│ MediaProjection│                                        │  phone frame  │
│ Accessibility │  ◀─────────────────────────────────── │  mouse/keys   │
└───────────────┘        control JSON (PC → phone)        └───────────────┘
```

Everything stays on your LAN. Nothing leaves your network.

---

## Repository layout

| Path        | What it is                                                        |
|-------------|-------------------------------------------------------------------|
| `android/`  | The phone agent — an Android Studio / Gradle project (Kotlin).     |
| `pc/`       | The PC controller — a .NET 8 WPF app (C#).                         |

---

## 1. Build & install the Android agent

Requirements: Android SDK (compileSdk 36), a JDK **17–21** (Gradle 8.11 does not
run on JDK 25), a phone on **Android 8.0 / API 26 or newer**.

```bash
cd android
./gradlew assembleDebug
```

The APK lands at `android/app/build/outputs/apk/debug/app-debug.apk`. Install it:

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

…or copy the APK to the phone and tap it (allow "install unknown apps").

> If your Gradle JDK is version 22+, the build will fail. Point Gradle at a
> JDK 17–21 by editing `org.gradle.java.home` in `android/gradle.properties`.

### On the phone, one-time setup

1. Open **PC Phone Connect**.
2. **Step 1 · Remote control** → tap *Enable control* and turn on
   "PC Phone Connect" under **Accessibility → Installed apps**. This is what lets
   the PC inject taps and swipes. (Screen mirroring works without it, but you
   won't be able to control the phone — only watch.)
3. **Step 2 · Screen sharing** → tap *Start server* and accept the
   "Start recording / casting" prompt.
4. The app shows the address(es) to connect to, e.g. `192.168.1.50 : 6060`.

---

## 2. Build & run the PC app

Requirements: **.NET 8 SDK** on Windows.

```bash
cd pc
dotnet run --project PCPhoneConnect
```

Or build a release exe:

```bash
dotnet build PCPhoneConnect.sln -c Release
# → pc/PCPhoneConnect/bin/Release/net8.0-windows/PCPhoneConnect.exe
```

1. Enter the phone's IP and port (from the phone app) and click **Connect**.
2. The live screen appears in the phone frame.

### Controls

| Action on PC                    | Effect on phone            |
|---------------------------------|----------------------------|
| Left click                      | Tap                        |
| Click + drag                    | Swipe (scroll / fling)     |
| Left click, hold ~0.5s, release | Long press                 |
| Right click                     | Back                       |
| `Esc` key                       | Back                       |
| `Home` key                      | Home                       |
| Back / Home / Recents / Notifs buttons | Navigation actions  |

---

## How it works

**Screen capture.** `StreamService` requests a `MediaProjection`, mirrors the
display into a `VirtualDisplay` backed by an `ImageReader`, downscales the longest
edge to ≤ 1280 px, encodes each frame to JPEG, and pushes it to the connected PC.

**Control.** `ControlAccessibilityService` is the only non-root way to synthesize
touch. The PC sends normalized (0..1) coordinates; the service maps them to real
device pixels and calls `dispatchGesture` (taps/swipes/long-press) or
`performGlobalAction` (Back/Home/Recents/Notifications).

### Wire protocol (single TCP socket, full-duplex)

Phone → PC, repeated:

```
[1 byte type][4 byte big-endian length][payload]
  type 0 = UTF-8 JSON header  {"name","w","h","sw","sh"}   (once per connection)
  type 1 = JPEG frame
```

PC → phone, repeated:

```
[4 byte big-endian length][UTF-8 JSON]
  {"t":"tap","x":0.5,"y":0.5}
  {"t":"long","x":..,"y":..,"dur":600}
  {"t":"swipe","x1":..,"y1":..,"x2":..,"y2":..,"dur":200}
  {"t":"key","k":"back|home|recents|notifications|lock|power"}
```

`w`/`h` are the real device pixels (used to map control coordinates); `sw`/`sh`
are the streamed frame size.

---

## Security & scope

- The connection is **plaintext and unauthenticated** — intended for a trusted
  home LAN only. Do not expose the port to the internet or an untrusted network.
- The Accessibility service can fully control the device while enabled; turn it
  off when you're not using remote control.
- No data is sent anywhere except to the PC that connects to the phone.

## Roadmap ideas

- Optional PIN / TLS on the socket.
- H.264 via `MediaCodec` for lower bandwidth (currently MJPEG for simplicity).
- Text input injection and clipboard sync.
