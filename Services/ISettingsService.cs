using System;
using BoltFetch.Models;

namespace BoltFetch.Services
{
    public interface ISettingsService
    {
        UserSettings Load();
        void Save(UserSettings settings);
    }
}
