using System.Globalization;

namespace Typedown.Uno.Services;

/// <summary>UI strings (English and Simplified Chinese). Menus are built in code, so a dictionary is enough.</summary>
public static class Loc
{
    private static readonly Dictionary<string, string> Zh = new()
    {
        ["File"] = "文件", ["New"] = "新建", ["NewTab"] = "新建标签", ["Open"] = "打开…", ["OpenFolder"] = "打开文件夹…", ["Recent"] = "最近打开",
        ["NoRecent"] = "（无）", ["ClearRecent"] = "清除列表", ["Save"] = "保存", ["SaveAs"] = "另存为…", ["ExportHtml"] = "导出 HTML…",
        ["ShareHedgeDoc"] = "分享到 HedgeDoc…", ["Settings"] = "设置…", ["CloseTab"] = "关闭标签", ["Exit"] = "退出",
        ["Edit"] = "编辑", ["Find"] = "查找…", ["FindNext"] = "查找下一个", ["FindPrevious"] = "查找上一个", ["SelectAll"] = "全选",
        ["Paragraph"] = "段落", ["Heading"] = "标题", ["HeadingN"] = "{0} 级标题", ["ParagraphPlain"] = "正文", ["IncreaseHeading"] = "提升标题级别",
        ["DecreaseHeading"] = "降低标题级别", ["Table"] = "表格…", ["CodeFences"] = "代码块", ["MathBlock"] = "公式块", ["Quote"] = "引用",
        ["QuoteIncrease"] = "增加引用层级", ["QuoteDecrease"] = "减少引用层级", ["OrderedList"] = "有序列表", ["UnorderedList"] = "无序列表",
        ["TaskList"] = "任务列表", ["InsertBefore"] = "在前面插入段落", ["InsertAfter"] = "在后面插入段落", ["Chart"] = "图表", ["Mermaid"] = "Mermaid",
        ["FlowChart"] = "流程图", ["Sequence"] = "时序图", ["VegaLite"] = "Vega-Lite", ["PlantUml"] = "PlantUML", ["HorizontalLine"] = "分割线",
        ["Toc"] = "目录", ["FrontMatter"] = "YAML Front Matter", ["Footnote"] = "脚注", ["LinkReference"] = "链接引用",
        ["Format"] = "格式", ["Strong"] = "加粗", ["Emphasis"] = "斜体", ["Underline"] = "下划线", ["InlineCode"] = "行内代码", ["InlineMath"] = "行内公式",
        ["Strikethrough"] = "删除线", ["Highlight"] = "高亮", ["Hyperlink"] = "链接", ["Image"] = "图片", ["ClearFormat"] = "清除格式",
        ["View"] = "视图", ["SourceCode"] = "源代码模式", ["FocusMode"] = "专注模式", ["Typewriter"] = "打字机模式", ["ReadOnly"] = "阅读模式",
        ["SidePane"] = "侧边栏", ["Theme"] = "主题", ["ThemeSystem"] = "跟随系统", ["ThemeLight"] = "浅色", ["ThemeDark"] = "深色", ["ThemeBlack"] = "纯黑",
        ["Help"] = "帮助", ["About"] = "关于",
        ["Files"] = "文件", ["Outline"] = "大纲", ["Search"] = "搜索", ["SearchPlaceholder"] = "在文件夹中搜索…", ["NoFolder"] = "没有打开文件夹",
        ["NoHeadings"] = "没有标题", ["NoResults"] = "没有结果", ["Results"] = "{0} 个文件中有 {1} 处匹配",
        ["Untitled"] = "未命名", ["Words"] = "{0} 字", ["SaveChanges"] = "保存更改？", ["HasUnsaved"] = "{0} 有未保存的更改。",
        ["DontSave"] = "不保存", ["Cancel"] = "取消", ["OK"] = "确定", ["FileChanged"] = "文件已在外部修改", ["ReloadPrompt"] = "{0} 在 Typedown 之外被修改了。重新加载并放弃你的更改？",
        ["Reload"] = "重新加载", ["KeepMine"] = "保留我的", ["CannotOpen"] = "无法打开文件", ["CannotSave"] = "无法保存文件", ["Error"] = "错误",
        ["FindPlaceholder"] = "查找", ["Close"] = "关闭",
        ["FilesSection"] = "文件", ["StartupAction"] = "启动时", ["StartupNewFile"] = "新建空白文档", ["StartupOpenLast"] = "打开上次的文件", ["StartupRestoreSession"] = "恢复上次打开的所有文档",
        ["FolderStartup"] = "启动时的文件夹", ["FolderStartupNone"] = "不打开", ["FolderStartupOpenLast"] = "上次的文件夹", ["FolderStartupOpenFixed"] = "指定文件夹",
        ["StartupFolder"] = "指定的文件夹", ["Browse"] = "浏览…", ["OpenFilesInNewTab"] = "在新标签中打开文件", ["StatusBar"] = "显示状态栏",
        ["WordCountMethod"] = "状态栏统计", ["CountWords"] = "词数", ["CountCharacters"] = "字符数", ["CountParagraphs"] = "段落数",
        ["AutoReload"] = "文件在外部被修改时自动重新加载", ["AutoReloadDescription"] = "即使当前有未保存的更改也直接重新加载", ["AskBeforeReload"] = "重新加载前询问",
        ["OpenFolderAfterExport"] = "导出后打开所在文件夹", ["TrimCodeBlock"] = "去掉代码块多余的空行",
        ["AutoPairBracket"] = "自动补全括号", ["AutoPairQuote"] = "自动补全引号", ["AutoPairMarkdown"] = "自动补全 Markdown 标记",
        ["TextDirection"] = "文字方向", ["Dirauto"] = "自动", ["Dirltr"] = "从左到右", ["Dirrtl"] = "从右到左",
        ["FontFamily"] = "字体", ["FontFamilyPlaceholder"] = "留空使用默认字体", ["CustomCss"] = "自定义 CSS",
        ["FindSection"] = "查找", ["FindCaseSensitive"] = "区分大小写", ["FindWholeWord"] = "全字匹配", ["FindRegex"] = "正则表达式",
        ["TestConnection"] = "测试连接", ["Test"] = "测试",
        ["InsertImage"] = "插入图片…", ["PrintPdf"] = "打印 / 导出 PDF…", ["PrintOpened"] = "已在浏览器中打开，用浏览器的打印功能保存为 PDF",
        ["NewFileHere"] = "在此新建文件", ["NewFolderHere"] = "在此新建文件夹", ["Rename"] = "重命名", ["Delete"] = "删除", ["CopyPath"] = "复制路径",
        ["RevealInFileManager"] = "在文件管理器中打开", ["Refresh"] = "刷新", ["DeleteConfirm"] = "确定删除 {0}？此操作不可撤销。",
        ["ImageSection"] = "图片", ["ImageAction"] = "插入图片时", ["ImageCopy"] = "复制到文档旁边", ["ImageKeep"] = "保留原路径",
        ["ImageCopyPath"] = "图片文件夹", ["ImageCopyPathHint"] = "支持 ${filename} ${filedir} ${year} ${month} ${day}",
        ["PreferRelativeImagePaths"] = "使用相对路径", ["EncodeImageLinks"] = "对链接中的空格等字符转义",
        ["FileName"] = "文件名", ["FolderName"] = "文件夹",
        ["ShortcutSection"] = "快捷键", ["ShortcutHint"] = "点击输入框后直接按组合键；Esc 清除，↺ 恢复默认", ["ShortcutNone"] = "（未设置）", ["ShortcutReset"] = "恢复默认",
        ["CmdNewTab"] = "新建标签", ["CmdOpen"] = "打开文件", ["CmdOpenFolder"] = "打开文件夹", ["CmdSave"] = "保存", ["CmdSaveAs"] = "另存为",
        ["CmdExportHtml"] = "导出 HTML", ["CmdPrint"] = "打印 / 导出 PDF", ["CmdShareHedgeDoc"] = "分享到 HedgeDoc", ["CmdSettings"] = "设置",
        ["CmdCloseTab"] = "关闭标签", ["CmdExit"] = "退出", ["CmdFind"] = "查找", ["CmdFindNext"] = "查找下一个", ["CmdFindPrevious"] = "查找上一个",
        ["CmdSearchInFolder"] = "在文件夹中搜索", ["CmdSelectAll"] = "全选", ["CmdNextTab"] = "下一个标签", ["CmdPreviousTab"] = "上一个标签",
        ["CmdSidePane"] = "侧边栏", ["CmdReadingMode"] = "阅读模式", ["CmdSourceCode"] = "源代码模式", ["CmdInsertImage"] = "插入图片",
        ["SearchInFolder"] = "在文件夹中搜索",
        ["NewWindow"] = "新建窗口", ["OpenInNewWindow"] = "在新窗口中打开", ["CmdNewWindow"] = "新建窗口",
        ["SettingsTitle"] = "设置", ["General"] = "通用", ["Language"] = "语言", ["LangSystem"] = "跟随系统", ["Appearance"] = "外观", ["Editor"] = "编辑器",
        ["FontSize"] = "字号", ["LineHeight"] = "行高", ["EditorWidth"] = "编辑区宽度", ["TabSize"] = "Tab 宽度", ["ListIndentation"] = "列表缩进",
        ["TableAlign"] = "对齐表格列", ["LooseList"] = "宽松列表", ["ParagraphMarker"] = "显示段落标记", ["Spellcheck"] = "拼写检查",
        ["AutoSave"] = "自动保存", ["RememberPosition"] = "记住光标和滚动位置", ["AlwaysShowTabBar"] = "始终显示标签栏",
        ["HedgeDoc"] = "HedgeDoc", ["Server"] = "服务器", ["Email"] = "邮箱（可选）", ["Password"] = "密码", ["PublishReadOnly"] = "同时生成只读链接",
        ["Shared"] = "已分享到 HedgeDoc", ["ReadOnlyLink"] = "只读链接", ["EditLink"] = "可编辑链接", ["CopyLink"] = "复制链接", ["OpenInBrowser"] = "在浏览器打开",
        ["NotConfigured"] = "请先在设置里填写 HedgeDoc 服务器地址。", ["EditableWarning"] = "拿到此链接的人都可以编辑这篇笔记。",
        ["Exported"] = "已导出：{0}", ["AboutText"] = "Typedown（Uno Platform 版）\n跨平台 Markdown 编辑器，编辑内核来自 MarkText/Muya。",
        ["AboutEditor"] = "编辑内核", ["AboutWebEngine"] = "网页引擎", ["AboutSystem"] = "系统", ["AboutSession"] = "桌面会话",
        ["CopyInfo"] = "复制信息", ["Copied"] = "已复制", ["AboutIssue"] = "反馈问题时请附上以上信息。",
        ["Copy"] = "复制", ["Cut"] = "剪切", ["Paste"] = "粘贴",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["File"] = "File", ["New"] = "New", ["NewTab"] = "New tab", ["Open"] = "Open…", ["OpenFolder"] = "Open folder…", ["Recent"] = "Open recent",
        ["NoRecent"] = "(none)", ["ClearRecent"] = "Clear list", ["Save"] = "Save", ["SaveAs"] = "Save as…", ["ExportHtml"] = "Export HTML…",
        ["ShareHedgeDoc"] = "Share to HedgeDoc…", ["Settings"] = "Settings…", ["CloseTab"] = "Close tab", ["Exit"] = "Exit",
        ["Edit"] = "Edit", ["Find"] = "Find…", ["FindNext"] = "Find next", ["FindPrevious"] = "Find previous", ["SelectAll"] = "Select all",
        ["Paragraph"] = "Paragraph", ["Heading"] = "Heading", ["HeadingN"] = "Heading {0}", ["ParagraphPlain"] = "Paragraph", ["IncreaseHeading"] = "Increase heading level",
        ["DecreaseHeading"] = "Decrease heading level", ["Table"] = "Table…", ["CodeFences"] = "Code fences", ["MathBlock"] = "Math block", ["Quote"] = "Quote",
        ["QuoteIncrease"] = "Increase quote level", ["QuoteDecrease"] = "Decrease quote level", ["OrderedList"] = "Ordered list", ["UnorderedList"] = "Unordered list",
        ["TaskList"] = "Task list", ["InsertBefore"] = "Insert paragraph before", ["InsertAfter"] = "Insert paragraph after", ["Chart"] = "Diagram", ["Mermaid"] = "Mermaid",
        ["FlowChart"] = "Flowchart", ["Sequence"] = "Sequence diagram", ["VegaLite"] = "Vega-Lite", ["PlantUml"] = "PlantUML", ["HorizontalLine"] = "Horizontal line",
        ["Toc"] = "Table of contents", ["FrontMatter"] = "YAML front matter", ["Footnote"] = "Footnote", ["LinkReference"] = "Link reference",
        ["Format"] = "Format", ["Strong"] = "Bold", ["Emphasis"] = "Italic", ["Underline"] = "Underline", ["InlineCode"] = "Inline code", ["InlineMath"] = "Inline math",
        ["Strikethrough"] = "Strikethrough", ["Highlight"] = "Highlight", ["Hyperlink"] = "Link", ["Image"] = "Image", ["ClearFormat"] = "Clear format",
        ["View"] = "View", ["SourceCode"] = "Source code mode", ["FocusMode"] = "Focus mode", ["Typewriter"] = "Typewriter mode", ["ReadOnly"] = "Reading mode",
        ["SidePane"] = "Side pane", ["Theme"] = "Theme", ["ThemeSystem"] = "System", ["ThemeLight"] = "Light", ["ThemeDark"] = "Dark", ["ThemeBlack"] = "Black",
        ["Help"] = "Help", ["About"] = "About",
        ["Files"] = "Files", ["Outline"] = "Outline", ["Search"] = "Search", ["SearchPlaceholder"] = "Search in folder…", ["NoFolder"] = "No folder open",
        ["NoHeadings"] = "No headings", ["NoResults"] = "No results", ["Results"] = "{1} matches in {0} files",
        ["Untitled"] = "Untitled", ["Words"] = "{0} words", ["SaveChanges"] = "Save changes?", ["HasUnsaved"] = "{0} has unsaved changes.",
        ["DontSave"] = "Don't save", ["Cancel"] = "Cancel", ["OK"] = "OK", ["FileChanged"] = "File changed on disk", ["ReloadPrompt"] = "{0} was modified outside Typedown. Reload it and lose your changes?",
        ["Reload"] = "Reload", ["KeepMine"] = "Keep mine", ["CannotOpen"] = "Cannot open file", ["CannotSave"] = "Cannot save file", ["Error"] = "Error",
        ["FindPlaceholder"] = "Find", ["Close"] = "Close",
        ["FilesSection"] = "Files", ["StartupAction"] = "On startup", ["StartupNewFile"] = "New empty document", ["StartupOpenLast"] = "Open the last file", ["StartupRestoreSession"] = "Restore all documents of the last session",
        ["FolderStartup"] = "Folder on startup", ["FolderStartupNone"] = "None", ["FolderStartupOpenLast"] = "Last folder", ["FolderStartupOpenFixed"] = "A fixed folder",
        ["StartupFolder"] = "Fixed folder", ["Browse"] = "Browse…", ["OpenFilesInNewTab"] = "Open files in a new tab", ["StatusBar"] = "Show the status bar",
        ["WordCountMethod"] = "Status bar count", ["CountWords"] = "Words", ["CountCharacters"] = "Characters", ["CountParagraphs"] = "Paragraphs",
        ["AutoReload"] = "Reload when the file changes on disk", ["AutoReloadDescription"] = "Reload even when there are unsaved changes", ["AskBeforeReload"] = "Ask before reloading",
        ["OpenFolderAfterExport"] = "Open the folder after exporting", ["TrimCodeBlock"] = "Trim unnecessary empty lines in code blocks",
        ["AutoPairBracket"] = "Auto-pair brackets", ["AutoPairQuote"] = "Auto-pair quotes", ["AutoPairMarkdown"] = "Auto-pair Markdown syntax",
        ["TextDirection"] = "Text direction", ["Dirauto"] = "Automatic", ["Dirltr"] = "Left to right", ["Dirrtl"] = "Right to left",
        ["FontFamily"] = "Font family", ["FontFamilyPlaceholder"] = "Empty = default font", ["CustomCss"] = "Custom CSS",
        ["FindSection"] = "Find", ["FindCaseSensitive"] = "Match case", ["FindWholeWord"] = "Whole word", ["FindRegex"] = "Regular expression",
        ["TestConnection"] = "Test connection", ["Test"] = "Test",
        ["InsertImage"] = "Insert image…", ["PrintPdf"] = "Print / export PDF…", ["PrintOpened"] = "Opened in the browser — use its print dialog to save as PDF",
        ["NewFileHere"] = "New file here", ["NewFolderHere"] = "New folder here", ["Rename"] = "Rename", ["Delete"] = "Delete", ["CopyPath"] = "Copy path",
        ["RevealInFileManager"] = "Show in file manager", ["Refresh"] = "Refresh", ["DeleteConfirm"] = "Delete {0}? This cannot be undone.",
        ["ImageSection"] = "Images", ["ImageAction"] = "When inserting an image", ["ImageCopy"] = "Copy next to the document", ["ImageKeep"] = "Keep the original path",
        ["ImageCopyPath"] = "Image folder", ["ImageCopyPathHint"] = "supports ${filename} ${filedir} ${year} ${month} ${day}",
        ["PreferRelativeImagePaths"] = "Use relative paths", ["EncodeImageLinks"] = "Escape spaces and brackets in links",
        ["FileName"] = "File name", ["FolderName"] = "Folder",
        ["ShortcutSection"] = "Shortcuts", ["ShortcutHint"] = "Click a box and press the combination; Esc clears it, ↺ restores the default", ["ShortcutNone"] = "(none)", ["ShortcutReset"] = "Restore the default",
        ["CmdNewTab"] = "New tab", ["CmdOpen"] = "Open file", ["CmdOpenFolder"] = "Open folder", ["CmdSave"] = "Save", ["CmdSaveAs"] = "Save as",
        ["CmdExportHtml"] = "Export HTML", ["CmdPrint"] = "Print / export PDF", ["CmdShareHedgeDoc"] = "Share to HedgeDoc", ["CmdSettings"] = "Settings",
        ["CmdCloseTab"] = "Close tab", ["CmdExit"] = "Exit", ["CmdFind"] = "Find", ["CmdFindNext"] = "Find next", ["CmdFindPrevious"] = "Find previous",
        ["CmdSearchInFolder"] = "Search in folder", ["CmdSelectAll"] = "Select all", ["CmdNextTab"] = "Next tab", ["CmdPreviousTab"] = "Previous tab",
        ["CmdSidePane"] = "Side pane", ["CmdReadingMode"] = "Reading mode", ["CmdSourceCode"] = "Source code mode", ["CmdInsertImage"] = "Insert image",
        ["SearchInFolder"] = "Search in folder",
        ["NewWindow"] = "New window", ["OpenInNewWindow"] = "Open in a new window", ["CmdNewWindow"] = "New window",
        ["SettingsTitle"] = "Settings", ["General"] = "General", ["Language"] = "Language", ["LangSystem"] = "System", ["Appearance"] = "Appearance", ["Editor"] = "Editor",
        ["FontSize"] = "Font size", ["LineHeight"] = "Line height", ["EditorWidth"] = "Editor width", ["TabSize"] = "Tab size", ["ListIndentation"] = "List indentation",
        ["TableAlign"] = "Align table columns", ["LooseList"] = "Loose list items", ["ParagraphMarker"] = "Show paragraph markers", ["Spellcheck"] = "Spell check",
        ["AutoSave"] = "Auto save", ["RememberPosition"] = "Remember caret and scroll position", ["AlwaysShowTabBar"] = "Always show the tab bar",
        ["HedgeDoc"] = "HedgeDoc", ["Server"] = "Server", ["Email"] = "Email (optional)", ["Password"] = "Password", ["PublishReadOnly"] = "Also publish a read-only link",
        ["Shared"] = "Shared to HedgeDoc", ["ReadOnlyLink"] = "Read-only link", ["EditLink"] = "Editable link", ["CopyLink"] = "Copy link", ["OpenInBrowser"] = "Open in browser",
        ["NotConfigured"] = "Set the HedgeDoc server address in Settings first.", ["EditableWarning"] = "Anyone with this link can edit the note.",
        ["Exported"] = "Exported: {0}", ["AboutText"] = "Typedown (Uno Platform edition)\nCross-platform Markdown editor; editing engine from MarkText/Muya.",
        ["AboutEditor"] = "Editor engine", ["AboutWebEngine"] = "Web engine", ["AboutSystem"] = "System", ["AboutSession"] = "Desktop session",
        ["CopyInfo"] = "Copy details", ["Copied"] = "Copied", ["AboutIssue"] = "Please include these details in a bug report.",
        ["Copy"] = "Copy", ["Cut"] = "Cut", ["Paste"] = "Paste",
    };

    public static string Language { get; private set; } = "en";

    public static void Apply(string? setting)
    {
        var lang = string.IsNullOrEmpty(setting) ? CultureInfo.CurrentUICulture.Name : setting;
        Language = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en";
    }

    public static string Get(string key) =>
        (Language == "zh-Hans" && Zh.TryGetValue(key, out var zh)) ? zh : En.TryGetValue(key, out var en) ? en : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);
}
