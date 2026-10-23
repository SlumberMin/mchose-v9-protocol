# mchose-v9-protocol — 迈从 V9 Turbo+ 耳机灯控协议

对 **迈从（MCHOSE）V9 Turbo+** 耳机 RGB 灯控的逆向与实测记录。
耳机在系统里是**两台 USB 设备**，灯控只在其中一台上：

| 地址 | 角色 | 说明 |
|---|---|---|
| `0x3837:0x6008` | **WCH 主控，灯控** | product `V9 Turbo`，mfr `wch.cn`，唯一 HID 集合 `usage_page 0xFF00` |
| `0x3837:0x600A` | C-Media 声卡 | 7.1 音频 + 电话 HID，**与灯无关，不要碰**（换驱动会掉声音） |

## 核心结论

1. 报文 **64 字节**，第一字节是 report id `0xAA`：

   ```
   aa 27 00 00 00 0a 01 <mode> 05 <dir> <bright> 00 <R> <G> <B> ...
   ^^                      ^^       ^^        ^^
   report id               [7]      [9]       [10]         [12..14]
   ```

2. **Windows 上只有 `WriteFile`（65 字节 = `[0x00] + 64 字节报文`）真正生效。**
   `HidD_SetOutputReport` 会返回成功、甚至回 ACK，**但固件不执行**——这正是“灯控协议都对、耳机就是不变色”的原因。
3. **读写必须用同一个句柄**，并且每写一帧都要把设备回包（ACK）排空；否则队列满了固件会**静默拒收**新帧。
4. 可以用 **`0x07` 状态查询**客观读回当前 RGB，不用靠肉眼：
   回包 `00 AA 07 02 00 00 1E 01 00 06 00 03 00 <R> <G> <B> ... "V9 Turbo" ...`
   → **RGB 在 offset 13..15**。

## 验证

```powershell
pwsh -File tools\build.ps1        # 编译 C# 探针
.\tools\headset_test.exe          # 依次尝试 4 种写入方式 + 0x07 读回
```

输出会显示每种方式写入后设备读回的颜色：

- `SetOutputReport(65)` → 不变；
- `WriteFile(65)` → 变；
- 63/64 字节 → 不生效。

Python 版最小验证：`python tools/mchose_fix.py`（需要 `pip install hidapi`）。

## 文档 / 工具

- [docs/PROTOCOL.md](docs/PROTOCOL.md) — 报文结构、模式表、ACK/句柄坑、读回方法、节流
- `tools/headset_test.cs` + `tools/HidNative.cs` — 自包含 C# 探针（WriteFile / SetOutputReport 对比 + RGB 读回）
- `tools/mchose_fix.py` — Python 最小验证脚本
- `tools/kill_mchose.py` — 关闭 M HUB（它会每 ~200 ms 重推自己的灯效，和你的程序抢设备）
- `tools/usb_pcap_parse.py` — USBPcap 解析（注意 headerLen 27/28）

## 相关仓库

- 灯效软件：[PrismSync](https://github.com/SlumberMin/PrismSync)
- 同系列研究：[clevo-wmi-rgb](https://github.com/SlumberMin/clevo-wmi-rgb) ·
  [aula-hero68-protocol](https://github.com/SlumberMin/aula-hero68-protocol)

## License / 免责

代码 MIT，文档 CC BY 4.0。与迈从（MCHOSE）无关联；逆向研究仅供学习，刷写风险自负。
