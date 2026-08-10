using Xunit;

// The integration tests in this assembly drive a single physical USB device
// (winusb allows only ONE open handle per ADB interface) and spawn child adb
// processes that also claim that interface. xUnit runs different test classes
// in parallel by default; concurrent USB access makes the device disconnect and
// reconnect (observed as repeated vibration), causes "interface busy" failures,
// and makes the run flaky. Serialize the whole assembly so only one test touches
// the device at a time. Emulator (TCP) tests are unaffected in correctness, just
// run sequentially.
// <para>本程序集中的集成测试会驱动单一物理 USB 设备（winusb 每个 ADB 接口仅允许
// 一个打开句柄），并派生同样会 claim 该接口的子 adb 进程。xUnit 默认并行运行不同
// 测试类；并发 USB 访问会导致设备断开重连（表现为反复振动）、"接口忙"失败以及运行
// 不稳定。串行化整个程序集，使同一时刻只有一个测试访问设备。模拟器（TCP）测试的
// 正确性不受影响，只是顺序执行。</para>
[assembly: CollectionBehavior(DisableTestParallelization = true)]
