using System;
using System.Collections;
using UnityEngine;
using Mirror;

namespace WeaverCommandTests.CommandStartsWithCmd
{
    class CommandStartsWithCmd : NetworkBehaviour
    {
        [Command]
        void DoesntStartWithCmd() {}
    }
}
