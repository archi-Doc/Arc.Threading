// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class TemplateBenchmark
{
    public TemplateBenchmark()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public void Clone_Raw()
    {
    }
}
