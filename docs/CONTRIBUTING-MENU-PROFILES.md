# 应用菜单 JSON 配置贡献指南 (Contributing Menu Profiles)

TahoeDesk 顶部菜单栏采用「三级绝对静默」策略：
1. **Win32 原生 `GetMenu`**：直接读取传统 Win32 应用菜单树。
2. **静态 UI Automation**：针对标准 UIA 菜单栏的只读提取（绝不触发弹窗或抢焦点）。
3. **内置高频应用 JSON 配置表**：为 Chrome、Edge、VS Code、微信、文件资源管理器等自绘标题栏应用提供原生级菜单与快捷键映射。

## 如何新增一个应用菜单配置

1. 在 `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/` 目录中新建或编辑 `<appname>.json`。
2. JSON 结构示例：

```json
{
  "processNames": ["Code", "Code - Insiders"],
  "displayName": "Visual Studio Code",
  "menus": [
    {
      "title": "文件",
      "children": [
        { "title": "新建文件", "shortcut": "Ctrl+N", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+N" } },
        { "title": "打开文件…", "shortcut": "Ctrl+O", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+O" } },
        { "isSeparator": true },
        { "title": "保存", "shortcut": "Ctrl+S", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+S" } }
      ]
    },
    {
      "title": "编辑",
      "children": [
        { "title": "撤销", "shortcut": "Ctrl+Z", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+Z" } },
        { "title": "重做", "shortcut": "Ctrl+Y", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+Y" } },
        { "isSeparator": true },
        { "title": "查找", "shortcut": "Ctrl+F", "action": { "kind": "SendHotkey", "hotkey": "Ctrl+F" } }
      ]
    }
  ]
}
```

3. 可用 `action.kind`：
   - `"SendHotkey"`：向目标前台窗口发送标准快捷键（如 `"Ctrl+S"`、`"Ctrl+Shift+P"`、`"Alt+F4"`）。
   - `"WindowCommand"`：`windowOp` 支持 `"minimize"` / `"maximize"` / `"restore"` / `"close"`。

4. 运行单元测试确认 JSON 解析与加载通过：

```powershell
dotnet test tests/GlassShell.Core.Tests
```
