# Changelog

## [1.0.1](https://github.com/nismangulov/hdr-switcher/compare/v1.0.0...v1.0.1) (2026-04-04)


### Bug Fixes

* use SystemEvents.DisplaySettingsChanged to detect external HDR changes ([55dedf4](https://github.com/nismangulov/hdr-switcher/commit/55dedf483983b5e466f362c8bf257e6dc2139246))

## 1.0.0 (2026-04-04)


### Features

* AutostartManager with registry read/write ([8fca9cc](https://github.com/nismangulov/hdr-switcher/commit/8fca9cc86f811127b80b408c56a5592d9d604d0d))
* embed golden sun application icon ([6367a42](https://github.com/nismangulov/hdr-switcher/commit/6367a42d69d72dc6da4b7a169f4e766024e93118))
* HDR Switcher implementation ([1a32efc](https://github.com/nismangulov/hdr-switcher/commit/1a32efc615f2401fde67c6e6ff7119f84dcfb0f2))
* HdrManager with Win32 QueryDisplayConfig P/Invoke ([cf6d12d](https://github.com/nismangulov/hdr-switcher/commit/cf6d12de1a3226336a623045de2d1ddf27fffe0a))
* IconRenderer with GDI+ vector sun icon, 3 states ([8e393b2](https://github.com/nismangulov/hdr-switcher/commit/8e393b27d7b934a3c08b35a46faebfc87f9c8311))
* TrayApplicationContext with tray icon, context menu, click handler ([fbf584f](https://github.com/nismangulov/hdr-switcher/commit/fbf584f43eef09982d4c8e791400bfd18fba48b1))
* Win11 native integration polish ([10ab705](https://github.com/nismangulov/hdr-switcher/commit/10ab705aca6cab10a90baf7141f41b2ab36b9be1))
* wire up Program.cs entry point ([9f2d8ac](https://github.com/nismangulov/hdr-switcher/commit/9f2d8ac620c6626851ea6da222cf1a0da0448585))


### Bug Fixes

* address code review findings ([c430b89](https://github.com/nismangulov/hdr-switcher/commit/c430b89a5457fe98ec399d0b67e41ec600722bea))
* correct HDR state detection and remove diagnostic code ([94f6c98](https://github.com/nismangulov/hdr-switcher/commit/94f6c98e43494a8a69ed1427feaf003e92c0bd6b))
* dispose GDI resources in IconRenderer tests, remove redundant brush ([214c4e6](https://github.com/nismangulov/hdr-switcher/commit/214c4e64ea783f68172247d3d5fc149fa6b8298b))


### Reverts

* use white icon for AllOn instead of accent colour ([43f6794](https://github.com/nismangulov/hdr-switcher/commit/43f6794caed47b0365d7fcc2daac72c288ae63ae))
