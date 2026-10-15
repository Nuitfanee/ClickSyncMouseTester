<h1 align="center">ClickSync Mouse Tester</h1>

<p align="center"><strong>English</strong> · <a href="README.zh-CN.md">简体中文</a></p>

<p align="center">
  <strong>Interactive charts for mouse performance analysis.</strong><br>
  Explore trajectories, X/Y counts, velocity, and report timing. Compare mice and recordings, test report rates, match sensitivity, calibrate angles, and inspect buttons, wheels, and keys.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square" alt="Windows x64">
  <img src="https://img.shields.io/badge/Rust-rewrite-DEA584?style=flat-square" alt="Rust rewrite">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-22C55E?style=flat-square" alt="MIT License"></a>
</p>

<p align="center">
  <a href="https://github.com/Nuitfanee/ClickSyncMouseTester/releases">Download</a> ·
  <a href="#features">Features</a> ·
  <a href="#getting-started">Getting Started</a>
</p>

A Windows mouse testing and analysis tool inspired by [MouseTester](https://github.com/microe1/MouseTester).
**Fully rewritten in Rust starting with v0.8.0**, with a GPUI interface, Direct3D 11 charts, and Windows Raw Input capture.

![Mouse performance analysis: trajectory, report intervals, and X/Y counts](UI/UI5.png)

## Features

| Feature | What you can do |
| --- | --- |
| **Mouse performance charts** | Record trajectories, X/Y counts, velocity, report intervals, and frequency. Test one mouse or compare two, pan and zoom, and inspect statistics for a selected time range |
| **Recording comparison** | Import and export full recordings as JSON, and overlay saved data for comparison |
| **Report rate testing** | View the live motion report rate, peak tier, empty-packet ratio, and a 3-second history |
| **Sensitivity matching** | Move two mice together to calculate the target mouse's recommended DPI and match the source mouse's translational sensitivity |
| **Angle calibration** | Analyze natural horizontal swipes for mean correction angle, left/right difference, and stroke variation; copy the result |
| **Button, wheel, and key detection** | Inspect press/release counts, double clicks, press intervals, and hold durations for five mouse buttons; test scrolling and a custom key |
| **Interface** | Switch between English and Simplified Chinese, and between dark and light themes |

## Screenshots

<details open>
<summary>View navigation, report rate testing, performance setup, angle calibration, and key detection</summary>

**Navigation**

![Feature navigation](UI/UI1.png)

**Report rate: setup and results**

![Report rate test setup](UI/UI2.png)
![Report rate test results](UI/UI3.png)

**Performance setup**

![Single-mouse and dual-mouse performance setup](UI/UI4.png)

**Angle calibration**

![Natural horizontal swipe angle calibration](UI/UI6.png)

**Key detection**

![Mouse button, wheel, and custom key detection](UI/UI7.png)

</details>

## Getting Started

### Launch

Supports **Windows x64**. Once you have a **v0.8.0 or newer** `.exe` release, double-click it to launch. See [Releases](https://github.com/Nuitfanee/ClickSyncMouseTester/releases) for published builds, or run from source.
Use the top-left menu to switch features and the top-right controls to change language and theme.

<details>
<summary>Run from source</summary>

Install rustup, the Visual Studio Build Tools C++ toolchain, and the Windows SDK. From the repository root, run:

```pwsh
cargo run --release -p clicksync-app --bin clicksync-app --locked
```

The Rust toolchain is pinned in `rust-toolchain.toml`.

</details>

### Mouse Performance Charts

1. Choose **Single mouse** or **Compare two mice**, select the devices, enter each mouse's actual DPI, and click **Open charts**.
2. Click the empty trajectory canvas or press `S` to start recording. Press `S` to pause or resume; use the toolbar to end or reset.
3. Drag to pan and scroll to zoom. Use **Explore metrics** for detailed charts and selected-range statistics, and **Import & export** to save full JSON recordings or add comparison data.

### Report Rate Testing

Select a mouse, click **Start test**, and keep moving it while watching the readout and history.
Press `Esc` or right-click to pause, resume to collect more data, or choose **End & clear** to reset. This page measures motion reports with non-zero displacement.

### Sensitivity Matching

1. Bind the **source mouse** and **target mouse** by activating each binding control and moving the corresponding mouse. Enter the target mouse's current DPI.
2. Proceed to measurement, press `S` or click start, and wait for the preparation prompt to finish. Hold the mice firmly side by side, facing the same direction, and move them together about **10 cm to the right**.
3. The measurement ends automatically. Apply the recommended DPI to the target mouse. Avoid sliding the mice against each other or rotating the pair during measurement.

### Angle Calibration

Set driver rotation to zero, disable angle snapping, and keep DPI fixed. Click the measurement area and make at least **30 valid strokes**, swiping naturally left and right with your usual grip.
Inspect the mean correction angle, left/right difference, and stroke variation. Press `Esc` or right-click to pause; click to resume. Copy the angle, apply it using your driver's sign convention, then reset and measure again.

### Button, Wheel, and Key Detection

Keep the window in the foreground, then press mouse buttons or scroll the wheel to view statistics.
Click **Choose key** and press the key you want to test. Adjust mouse and custom-key double-click thresholds independently; use **Reset statistics** to start counting again.

## License

Licensed under the [MIT License](LICENSE). Third-party dependencies and fonts retain their respective licenses.
