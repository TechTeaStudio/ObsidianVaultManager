using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VaultManager.App.ViewModels;

namespace VaultManager.App.Views;

public sealed partial class MainView : UserControl
{
	readonly Control? _picker;
	readonly Control? _addPlugin;
	readonly Control? _menu;
	readonly Control? _menuButton;
	ShellViewModel? _watched;

	public MainView()
	{
		AvaloniaXamlLoader.Load(this);
		Control? titleBar = this.FindControl<Control>("TitleBar");
		_picker = this.FindControl<Control>("Picker");
		_addPlugin = this.FindControl<Control>("AddPluginButton");
		_menu = this.FindControl<Control>("Menu");
		_menuButton = this.FindControl<Control>("MenuButton");

		if (titleBar is not null)
		{
			titleBar.PointerPressed += OnTitlePressed;
			titleBar.DoubleTapped += (_, e) =>
			{
				if (!IsInsideButton(e.Source))
				{
					ToggleMaximized();
				}
			};
		}

		Hook("MinimizeButton", () =>
		{
			if (TopLevel.GetTopLevel(this) is Window window)
			{
				window.WindowState = WindowState.Minimized;
			}
		});
		Hook("MaximizeButton", ToggleMaximized);
		Hook("CloseButton", () => (TopLevel.GetTopLevel(this) as Window)?.Close());
		if (!OperatingSystem.IsWindows())
		{
			foreach (string name in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
			{
				if (this.FindControl<Button>(name) is { } button)
				{
					button.IsVisible = false;
				}
			}
		}

		AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
		AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);

		DataContextChanged += (_, _) =>
		{
			if (_watched is not null)
			{
				_watched.PropertyChanged -= OnShellChanged;
			}

			_watched = DataContext as ShellViewModel;
			if (_watched is not null)
			{
				_watched.PropertyChanged += OnShellChanged;
			}
		};
		DetachedFromVisualTree += (_, _) =>
		{
			if (_watched is not null)
			{
				_watched.PropertyChanged -= OnShellChanged;
			}
		};
	}

	void OnShellChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ShellViewModel.IsMenuOpen) && _watched is { IsMenuOpen: true })
		{
			// The keyboard goes with the menu, or its items would only be reached after the whole window.
			Dispatcher.UIThread.Post(() => _menu?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyEnabled)?.Focus(), DispatcherPriority.Input);
		}
	}

	void Hook(string name, Action action)
	{
		if (this.FindControl<Button>(name) is { } button)
		{
			button.Click += (_, _) => action();
		}
	}

	void ToggleMaximized()
	{
		if (TopLevel.GetTopLevel(this) is Window window)
		{
			window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		}
	}

	void OnTitlePressed(object? sender, PointerPressedEventArgs e)
	{
		if (!IsInsideButton(e.Source) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window window)
		{
			window.BeginMoveDrag(e);
		}
	}

	static bool IsInsideButton(object? source) =>
		source is Visual visual && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any());

	// A press outside closes what floats, except on the Changes panel, which the picker's work lands in.
	void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (DataContext is not ShellViewModel shell || e.Source is not Visual source)
		{
			return;
		}

		if (shell.Picker.IsOpen && !source.GetSelfAndVisualAncestors().Any(v => v == _picker || v == _addPlugin || v is ChangesPanel))
		{
			shell.Picker.Close();
		}

		if (shell.IsMenuOpen && !source.GetSelfAndVisualAncestors().Any(v => v == _menu || v == _menuButton))
		{
			shell.CloseMenu();
		}
	}

	void OnAnyKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape || DataContext is not ShellViewModel shell)
		{
			return;
		}

		if (shell.IsMenuOpen)
		{
			shell.CloseMenu();
			_menuButton?.Focus();
			e.Handled = true;
		}
		else if (shell.Picker.IsOpen)
		{
			shell.Picker.Close();
			_addPlugin?.Focus();
			e.Handled = true;
		}
		else if (shell.Online.IsOpen)
		{
			shell.Online.CancelCommand.Execute(null);
			e.Handled = true;
		}
	}
}
