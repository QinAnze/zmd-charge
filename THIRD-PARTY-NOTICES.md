# 第三方声明

本项目的 HUD 图标资源（`Styles/Geometries.axaml`）内嵌了以下开源图标。
它们已从各自的原始网格等比归一化到统一的 24×24，以 `StreamGeometry` /
`GeometryGroup` 形式存放，运行时不依赖外部字体或网络资源。

| 资源键 | 来源图标库 | 原图标名 | 许可证 | 使用位置 |
| --- | --- | --- | --- | --- |
| `Geo.DeveloperBoard` | Material Symbols · Sharp Filled | `developer_board` | Apache-2.0 | 功耗模式设备徽章 |
| `Geo.LaptopMac` | Material Symbols · Sharp Filled | `laptop_mac` | Apache-2.0 | 电量模式设备徽章 |
| `Geo.Speedometer` | Bootstrap Icons | `speedometer2` | MIT | 性能面板方形态图标 |
| `Geo.Leaf` | Phosphor Icons · Fill | `leaf-fill` | MIT | 省电模式电标 |
| `Geo.Cable` | Material Symbols · Sharp Filled | `cable` | Apache-2.0 | 外接适配器药丸徽章 |

## Material Symbols

- 来源：https://github.com/google/material-design-icons
- 打包通道：https://www.npmjs.com/package/@material-symbols/svg-400
- 许可证：Apache License, Version 2.0
- 完整文本：https://www.apache.org/licenses/LICENSE-2.0

> Licensed under the Apache License, Version 2.0 (the "License");
> you may not use this file except in compliance with the License.
> You may obtain a copy of the License at
> http://www.apache.org/licenses/LICENSE-2.0

## Bootstrap Icons

- 来源：https://github.com/twbs/icons
- 许可证：MIT License
- 版权：Copyright (c) 2019-2024 The Bootstrap Authors
- 完整文本：https://github.com/twbs/icons/blob/main/LICENSE

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.

## Phosphor Icons

- 来源：https://github.com/phosphor-icons/core
- 许可证：MIT License
- 版权：Copyright (c) 2021-2024 Phosphor Icons
- 完整文本：https://github.com/phosphor-icons/core/blob/main/LICENSE

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.

## 运行时依赖（非图标）

| 组件 | 版本 | 许可证 |
| --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.7-pre731 | MPL-2.0 |
| Avalonia | 11.2.1 | MIT |
| System.Diagnostics.PerformanceCounter | 8.0.0 | MIT |
| System.Management | 10.0.11 | MIT |

## 分发提示

MIT 与 Apache-2.0 均要求二进制分发物保留上述版权与许可声明；Apache-2.0
另要求随附许可证全文。若发布安装包，建议同时把 Apache-2.0 与 MPL-2.0
全文放进 `LICENSES/` 目录，或直接引用本文件并附全文链接。

本项目自身的许可证见根目录 `LICENSE`（MIT）。
