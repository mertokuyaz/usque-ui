// Avoid the ambiguity between Usque.Core.Localization and System.Windows.Localization
// that WPF introduces; every Desktop file uses AppLocalization for UI translations.
global using AppLocalization = Usque.Core.Localization;
