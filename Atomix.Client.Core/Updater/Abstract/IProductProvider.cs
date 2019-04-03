﻿using System;

namespace Atomix.Updater.Abstract
{
    public interface IProductProvider
    {
        string Extension { get; }
        Version GetInstalledVersion();
        bool VerifyPackage(string packagePath);
        bool VerifyPackageVersion(string packagePath, Version version);
        void RunInstallation(string packagePath);
    }
}
