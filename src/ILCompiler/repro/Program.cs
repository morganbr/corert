// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

#pragma warning disable 219, 169 // unused local
internal static class Program
{
    private static unsafe void Main(string[] args)
    {
        TwoCharStr strStruct = new TwoCharStr();
        strStruct.first = (byte)'H';
        strStruct.second = (byte)'\0';
        printf((byte*)&strStruct, null);
    }

    [DllImport("*")]
    private static unsafe extern int printf(byte* str, byte* unused);
}

public struct TwoCharStr
{
    public byte first;
    public byte second;
}

