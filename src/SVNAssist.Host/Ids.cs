using System;

namespace SVNAssist.Host
{
    /// <summary>GUID e ID: devono coincidere con SVNAssist.vsct.</summary>
    internal static class Ids
    {
        public const string PackageGuid = "7a3b2c1d-4e5f-4a60-8b71-9c0d1e2f3a41";
        public const string ChangesWindowGuid = "2b4c6d8e-1a3c-4e5f-9a7b-0c1d2e3f4a52";
        public const string LogWindowGuid = "3c5d7e9f-2b4d-4f60-8b8c-1d2e3f4a5b63";
        public const string CmdSetGuid = "4d6e8f0a-3c5e-4071-9c9d-2e3f4a5b6c74";
        public const string OptionsPageGuid = "5e7f9a1b-4d6f-4182-8dae-3f4a5b6c7d85";

        public static readonly Guid CmdSet = new Guid(CmdSetGuid);

        public const int CmdChanges = 0x0100, CmdCommit = 0x0101, CmdUpdate = 0x0102, CmdLog = 0x0103, CmdAiSetup = 0x0104;
        public const int CmdUpdateRev = 0x0105, CmdCleanup = 0x0106, CmdSwitch = 0x0107, CmdMerge = 0x0108;
        public const int CmdLangAuto = 0x011B, CmdLangIt = 0x011C, CmdLangEn = 0x011D, CmdRevertSel = 0x011E, CmdNextChange = 0x011F, CmdPrevChange = 0x0120;
    }
}
