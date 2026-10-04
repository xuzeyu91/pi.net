using Xunit;

// 本项目测试禁并行：Facet 内核测试涉及共享 Console/时序敏感断言。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
