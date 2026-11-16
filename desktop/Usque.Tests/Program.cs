// Dependency-free tests: no real routing, firewall rules, or Cloudflare accounts.
return await new TestRunner(Console.Out, Console.Error).RunAsync(args,
    [.. CoreTests.All, .. RegressionTests.All, .. FeatureTests.All, .. ConnectivityTests.All, .. UiRegressionTests.All,
     .. TrayPresentationTests.All, .. TestRunnerTests.All]);
