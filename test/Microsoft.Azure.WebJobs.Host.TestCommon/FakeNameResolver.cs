﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;

namespace Microsoft.Azure.WebJobs.Host.TestCommon
{
    public class FakeNameResolver : INameResolver
    {
        public IDictionary<string, string> _dict = new Dictionary<string, string>();

        public string Resolve(string name)
        {
            string value;
            if (_dict.TryGetValue(name, out value))
            {
                return value;
            }

            return null;
        }

        // Fluid method for adding entries.
        public FakeNameResolver Add(string key, string value)
        {
            _dict[key] = value;
            return this;
        }
    }
}