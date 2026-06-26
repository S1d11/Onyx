// WinForms (used for the native tray NotifyIcon) and WPF both expose an
// 'Application' type and a 'MessageBox' type. Pin the WPF ones globally so
// they win everywhere in the app.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
