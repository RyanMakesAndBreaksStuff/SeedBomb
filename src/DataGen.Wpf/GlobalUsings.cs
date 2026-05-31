// Global using aliases to resolve namespace ambiguity between DataGen.Wpf
// and the Wpf.Ui.* packages. Without this alias, the C# compiler resolves
// `Wpf.Ui.Controls` as DataGen.Wpf.Ui.Controls because `DataGen` contains
// a `Wpf` child namespace, shadowing the global Wpf.Ui assembly.
global using Wpf = global::Wpf;
