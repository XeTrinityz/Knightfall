#nullable enable

using BmSDK.Engine;

namespace Knightfall;

// The only Script class. It holds no logic of its own.
[Script("Knightfall")]
public sealed class Entry : Script
{
    // Main runs on a normal start and OnLoad after a hot reload.
    public override void Main() => Kernel.Boot(this);

    public override void OnLoad() => Kernel.Boot(this);

    public override void OnUnload() => Kernel.Shutdown();

    public override void OnEnterMenu() => Kernel.EnterMenu();

    public override void OnEnterGame() => Kernel.EnterGame();

    public override void OnTick() => Kernel.Tick();

    public override void OnDraw(Canvas canvas) => Kernel.Frame(canvas);
}
