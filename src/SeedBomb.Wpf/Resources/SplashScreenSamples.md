# SeedBomb Splash Screen Samples

These samples use the new logo files wired into `src/SeedBomb.Wpf/SeedBomb.Wpf.csproj`.
The production startup splash currently uses WPF's native `<SplashScreen />` item with
`Resources\SB_logo_preview_256.png`, so it appears before `App.OnStartup` runs and closes
automatically when the first window renders.

## Sample 1: Native Minimal

Use this for the first implementation because it has no startup code path.

```xml
<ItemGroup>
  <SplashScreen Include="Resources\SB_logo_preview_256.png" />
</ItemGroup>
```

## Sample 2: Branded Shell Overlay

Use this if the app later needs a controllable splash overlay after the native splash closes.

```xml
<Grid Background="{DynamicResource DG.Bg}">
  <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
    <Image Source="pack://application:,,,/Resources/SB_logo.ico"
           Width="96"
           Height="96"
           Stretch="Uniform"/>
    <TextBlock Style="{DynamicResource DG.PageTitle}"
               Text="SeedBomb"
               HorizontalAlignment="Center"
               Margin="0,20,0,0"/>
    <TextBlock Style="{DynamicResource DG.PageSubtitle}"
               Text="Preparing mock data..."
               HorizontalAlignment="Center"
               Margin="0,8,0,0"/>
  </StackPanel>
</Grid>
```

## Sample 3: Dark Launch Panel

Use this for a custom splash window on dark backgrounds.

```xml
<Border Background="#181A17" Padding="36">
  <Grid Width="360" Height="220">
    <Grid.RowDefinitions>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <Image Source="pack://application:,,,/Resources/SB_logo_darkbg.ico"
           Width="112"
           Height="112"
           Stretch="Uniform"
           HorizontalAlignment="Center"
           VerticalAlignment="Center"/>
    <ProgressBar Grid.Row="1"
                 Height="4"
                 IsIndeterminate="True"
                 Foreground="{DynamicResource DG.RunSweep}"
                 Background="{DynamicResource DG.Surface2}"/>
  </Grid>
</Border>
```
