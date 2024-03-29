using ExitGames.Client.Photon;
using ExitGames.Client.Photon.Lite;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mod;
using Mod.gui;
using Mod.manager;
using Mod.mods;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Hashtable = ExitGames.Client.Photon.Hashtable;

internal class NetworkingPeer : LoadbalancingPeer, IPhotonPeerListener
{
    private HashSet<int> allowedReceivingGroups;
    private HashSet<int> blockSendingGroups;
    protected internal short currentLevelPrefix;
    protected internal const string CurrentSceneProperty = "curScn";
    private bool didAuthenticate;
    private IPhotonPeerListener externalListener;
    private string[] friendListRequested;
    private int friendListTimestamp;
    public bool hasSwitchedMC;
    public bool insideLobby;
    public Dictionary<int, GameObject> instantiatedObjects;
    private bool isFetchingFriends;
    public bool IsInitialConnect;
    protected internal bool loadingLevelAndPausedNetwork;
    public Dictionary<int, PhotonPlayer> mActors;
    protected internal string mAppId;
    protected internal string mAppVersion;
    public Dictionary<string, RoomInfo> mGameList;
    public RoomInfo[] mGameListCopy;
    private JoinType mLastJoinType;
    public PhotonPlayer mMasterClient;
    private Dictionary<System.Type, List<MethodInfo>> monoRPCMethodsCache;
    public PhotonPlayer[] mOtherPlayerListCopy;
    public PhotonPlayer[] mPlayerListCopy;
    private bool mPlayernameHasToBeUpdated;
    public string NameServerAddress;
    protected internal Dictionary<int, PhotonView> photonViewList;
    private string playername;
    public static Dictionary<string, GameObject> PrefabCache = new Dictionary<string, GameObject>();
    private static readonly Dictionary<ConnectionProtocol, int> ProtocolToNameServerPort;
    public bool requestSecurity;
    private readonly Dictionary<string, int> rpcShortcuts;
    private Dictionary<int, object[]> tempInstantiationData;
    public static bool UsePrefabCache = true;

    static NetworkingPeer()
    {
        Dictionary<ConnectionProtocol, int> dictionary = new Dictionary<ConnectionProtocol, int>();
        dictionary.Add(ConnectionProtocol.Udp, 5058);
        dictionary.Add(ConnectionProtocol.Tcp, 4533);
        ProtocolToNameServerPort = dictionary;
    }

    public NetworkingPeer(IPhotonPeerListener listener, string playername, ConnectionProtocol connectionProtocol) : base(listener, connectionProtocol)
    {
        this.playername = string.Empty;
        mActors = new Dictionary<int, PhotonPlayer>();
        mOtherPlayerListCopy = new PhotonPlayer[0];
        mPlayerListCopy = new PhotonPlayer[0];
        requestSecurity = true;
        monoRPCMethodsCache = new Dictionary<Type, List<MethodInfo>>();
        mGameList = new Dictionary<string, RoomInfo>();
        mGameListCopy = new RoomInfo[0];
        instantiatedObjects = new Dictionary<int, GameObject>();
        allowedReceivingGroups = new HashSet<int>();
        blockSendingGroups = new HashSet<int>();
        photonViewList = new Dictionary<int, PhotonView>();
        NameServerAddress = "ns.exitgamescloud.com";
        tempInstantiationData = new Dictionary<int, object[]>();
        if (PhotonHandler.PingImplementation == null)
        {
            PhotonHandler.PingImplementation = typeof(PingMono);
        }
        Listener = this;
        lobby = TypedLobby.Default;
        LimitOfUnreliableCommands = 40;
        externalListener = listener;
        PlayerName = playername;
        mLocalActor = new PhotonPlayer(true, -1, this.playername);
        AddNewPlayer(mLocalActor.ID, mLocalActor);
        rpcShortcuts = new Dictionary<string, int>(PhotonNetwork.PhotonServerSettings.RpcList.Count);
        for (int i = 0; i < PhotonNetwork.PhotonServerSettings.RpcList.Count; i++)
        {
            string str = PhotonNetwork.PhotonServerSettings.RpcList[i];
            rpcShortcuts[str] = i;
        }
        states = PeerStates.PeerCreated;
    }

    private void AddNewPlayer(int ID, PhotonPlayer player)
    {
        if (!mActors.ContainsKey(ID))
        {
            mActors[ID] = player;
            RebuildPlayerListCopies();
        }
        else
        {
            Debug.LogError("Adding player twice: " + ID);
        }
    }

    private bool AlmostEquals(object[] lastData, object[] currentContent)
    {
        if (lastData != null || currentContent != null)
        {
            if (lastData == null || currentContent == null || lastData.Length != currentContent.Length)
            {
                return false;
            }
            for (int i = 0; i < currentContent.Length; i++)
            {
                object one = currentContent[i];
                object two = lastData[i];
                if (!ObjectIsSameWithInprecision(one, two))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public void ChangeLocalID(int newID)
    {
        if (mLocalActor == null)
        {
            Debug.LogWarning($"Local actor is null or not in mActors! mLocalActor: {mLocalActor} mActors==null: {mActors == null} newID: {newID}");
        }
        if (mActors.ContainsKey(mLocalActor.ID))
        {
            mActors.Remove(mLocalActor.ID);
        }
        mLocalActor.InternalChangeLocalID(newID);
        mActors[mLocalActor.ID] = mLocalActor;
        RebuildPlayerListCopies();
    }

    public void checkLAN()
    {
        if (FengGameManagerMKII.OnPrivateServer && MasterServerAddress != null && MasterServerAddress != string.Empty && mGameserver != null && mGameserver != string.Empty && MasterServerAddress.Contains(":") && mGameserver.Contains(":"))
        {
            mGameserver = MasterServerAddress.Split(':')[0] + ":" + mGameserver.Split(':')[1];
        }
    }

    private void CheckMasterClient(int leavingPlayerId)
    {
        bool flag2;
        bool flag = mMasterClient != null && mMasterClient.ID == leavingPlayerId;
        if (!(flag2 = leavingPlayerId > 0) || flag)
        {
            if (mActors.Count <= 1)
            {
                mMasterClient = mLocalActor;
            }
            else
            {
                int num = 2147483647;
                foreach (int num2 in mActors.Keys)
                {
                    if (num2 < num && num2 != leavingPlayerId)
                    {
                        num = num2;
                    }
                }
                mMasterClient = mActors[num];
            }
            if (flag2)
            {
                object[] parameters = new object[] { mMasterClient };
                SendMonoMessage(PhotonNetworkingMessage.OnMasterClientSwitched, parameters);
            }
        }
    }

    private bool CheckTypeMatch(ParameterInfo[] methodParameters, Type[] callParameterTypes)
    {
        if (methodParameters.Length < callParameterTypes.Length)
        {
            return false;
        }
        for (int i = 0; i < callParameterTypes.Length; i++)
        {
            Type parameterType = methodParameters[i].ParameterType;
            if (callParameterTypes[i] != null && !parameterType.Equals(callParameterTypes[i]))
            {
                return false;
            }
        }
        return true;
    }

    public void CleanRpcBufferIfMine(PhotonView view)
    {
        if (view.ownerId != mLocalActor.ID && !mLocalActor.isMasterClient)
        {
            Debug.LogError(string.Concat("Cannot remove cached RPCs on a PhotonView thats not ours! ", view.owner, " scene: ", view.isSceneView));
        }
        else
        {
            OpCleanRpcBuffer(view);
        }
    }

    public bool Connect(string serverAddress, ServerConnection type)
    {
        bool flag;
        if (PhotonHandler.AppQuits)
        {
            Debug.LogWarning("Ignoring Connect() because app gets closed. If this is an error, check PhotonHandler.AppQuits.");
            return false;
        }
        if (PhotonNetwork.connectionStatesDetailed == PeerStates.Disconnecting)
        {
            Debug.LogError("Connect() failed. Can't connect while disconnecting (still). Current state: " + PhotonNetwork.connectionStatesDetailed);
            return false;
        }
        if (flag = base.Connect(serverAddress, string.Empty))
        {
            switch (type)
            {
                case ServerConnection.MasterServer:
                    states = PeerStates.ConnectingToMasterserver;
                    return flag;

                case ServerConnection.GameServer:
                    states = PeerStates.ConnectingToGameserver;
                    return flag;

                case ServerConnection.NameServer:
                    states = PeerStates.ConnectingToNameServer;
                    return flag;
            }
        }
        return flag;
    }

    public override bool Connect(string serverAddress, string applicationName)
    {
        Debug.LogError("Avoid using this directly. Thanks.");
        return false;
    }

    public bool ConnectToNameServer()
    {
        if (PhotonHandler.AppQuits)
        {
            Debug.LogWarning("Ignoring Connect() because app gets closed. If this is an error, check PhotonHandler.AppQuits.");
            return false;
        }
        IsUsingNameServer = true;
        CloudRegion = CloudRegionCode.none;
        if (states != PeerStates.ConnectedToNameServer)
        {
            string nameServerAddress = NameServerAddress;
            if (!nameServerAddress.Contains(":"))
            {
                int num = 0;
                ProtocolToNameServerPort.TryGetValue(UsedProtocol, out num);
                nameServerAddress = string.Format("{0}:{1}", nameServerAddress, num);
                Debug.Log(string.Concat("Server to connect to: ", nameServerAddress, " settings protocol: ", PhotonNetwork.PhotonServerSettings.Protocol));
            }
            if (!base.Connect(nameServerAddress, "ns"))
            {
                return false;
            }
            states = PeerStates.ConnectingToNameServer;
        }
        return true;
    }

    public bool ConnectToRegionMaster(CloudRegionCode region)
    {
        if (PhotonHandler.AppQuits)
        {
            Debug.LogWarning("Ignoring Connect() because app gets closed. If this is an error, check PhotonHandler.AppQuits.");
            return false;
        }
        IsUsingNameServer = true;
        CloudRegion = region;
        if (states == PeerStates.ConnectedToNameServer)
        {
            return OpAuthenticate(mAppId, mAppVersionPun, PlayerName, CustomAuthenticationValues, region.ToString());
        }
        string nameServerAddress = NameServerAddress;
        if (!nameServerAddress.Contains(":"))
        {
            int num = 0;
            ProtocolToNameServerPort.TryGetValue(UsedProtocol, out num);
            nameServerAddress = string.Format("{0}:{1}", nameServerAddress, num);
        }
        if (!base.Connect(nameServerAddress, "ns"))
        {
            return false;
        }
        states = PeerStates.ConnectingToNameServer;
        return true;
    }

    public void DebugReturn(DebugLevel level, string message)
    {
        externalListener.DebugReturn(level, message);
    }

    private bool DeltaCompressionRead(PhotonView view, Hashtable data)
    {
        if (!data.ContainsKey((byte) 1))
        {
            if (view.lastOnSerializeDataReceived == null)
            {
                return false;
            }
            object[] objArray = data[(byte) 2] as object[];
            if (objArray == null)
            {
                return false;
            }
            int[] target = data[(byte) 3] as int[];
            if (target == null)
            {
                target = new int[0];
            }
            object[] lastOnSerializeDataReceived = view.lastOnSerializeDataReceived;
            for (int i = 0; i < objArray.Length; i++)
            {
                if (objArray[i] == null && !target.Contains(i))
                {
                    objArray[i] = lastOnSerializeDataReceived[i];
                }
            }
            data[(byte) 1] = objArray;
        }
        return true;
    }

    private bool DeltaCompressionWrite(PhotonView view, Hashtable data)
    {
        if (view.lastOnSerializeDataSent != null)
        {
            object[] lastOnSerializeDataSent = view.lastOnSerializeDataSent;
            object[] objArray2 = data[(byte) 1] as object[];
            if (objArray2 == null)
            {
                return false;
            }
            if (lastOnSerializeDataSent.Length != objArray2.Length)
            {
                return true;
            }
            object[] objArray3 = new object[objArray2.Length];
            int num = 0;
            List<int> list = new List<int>();
            for (int i = 0; i < objArray3.Length; i++)
            {
                object one = objArray2[i];
                object two = lastOnSerializeDataSent[i];
                if (ObjectIsSameWithInprecision(one, two))
                {
                    num++;
                }
                else
                {
                    objArray3[i] = objArray2[i];
                    if (one == null)
                    {
                        list.Add(i);
                    }
                }
            }
            if (num > 0)
            {
                data.Remove((byte) 1);
                if (num == objArray2.Length)
                {
                    return false;
                }
                data[(byte) 2] = objArray3;
                if (list.Count > 0)
                {
                    data[(byte) 3] = list.ToArray();
                }
            }
        }
        return true;
    }

    public void DestroyAll(bool localOnly)
    {
        if (!localOnly)
        {
            OpRemoveCompleteCache();
            SendDestroyOfAll();
        }
        LocalCleanupAnythingInstantiated(true);
    }

    public void DestroyPlayerObjects(int playerId, bool localOnly)
    {
        if (playerId <= 0)
        {
            Debug.LogError("Failed to Destroy objects of playerId: " + playerId);
        }
        else
        {
            if (!localOnly)
            {
                OpRemoveFromServerInstantiationsOfPlayer(playerId);
                OpCleanRpcBuffer(playerId);
                SendDestroyOfPlayer(playerId);
            }
            Queue<GameObject> queue = new Queue<GameObject>();
            int num = playerId * PhotonNetwork.MAX_VIEW_IDS;
            int num2 = num + PhotonNetwork.MAX_VIEW_IDS;
            foreach (KeyValuePair<int, GameObject> pair in instantiatedObjects)
            {
                if (pair.Key > num && pair.Key < num2)
                {
                    queue.Enqueue(pair.Value);
                }
            }
            foreach (GameObject obj2 in queue)
            {
                RemoveInstantiatedGO(obj2, true);
            }
        }
    }

    public override void Disconnect()
    {
        if (PeerState == PeerStateValue.Disconnected)
        {
            if (!PhotonHandler.AppQuits)
            {
                Debug.LogWarning(string.Format("Can't execute Disconnect() while not connected. Nothing changed. State: {0}", states));
            }
        }
        else
        {
            states = PeerStates.Disconnecting;
            base.Disconnect();
        }
    }

    private void DisconnectToReconnect()
    {
        switch (server)
        {
            case ServerConnection.MasterServer:
                states = PeerStates.DisconnectingFromMasterserver;
                base.Disconnect();
                break;

            case ServerConnection.GameServer:
                states = PeerStates.DisconnectingFromGameserver;
                base.Disconnect();
                break;

            case ServerConnection.NameServer:
                states = PeerStates.DisconnectingFromNameServer;
                base.Disconnect();
                break;
        }
    }

    private void DisconnectToReconnect2()
    {
        checkLAN();
        switch (server)
        {
            case ServerConnection.MasterServer:
                states = FengGameManagerMKII.returnPeerState(2);
                base.Disconnect();
                break;

            case ServerConnection.GameServer:
                states = FengGameManagerMKII.returnPeerState(3);
                base.Disconnect();
                break;

            case ServerConnection.NameServer:
                states = FengGameManagerMKII.returnPeerState(4);
                base.Disconnect();
                break;
        }
    }

    internal GameObject DoInstantiate(Hashtable evData, PhotonPlayer photonPlayer, GameObject resourceGameObject)
    {
        Vector3 zero;
        int[] numArray;
        object[] objArray;
        string key = (string) evData[(byte) 0];
        int timestamp = (int) evData[(byte) 6];
        int instantiationId = (int) evData[(byte) 7];
        if (evData.ContainsKey((byte) 1))
        {
            zero = (Vector3) evData[(byte) 1];
        }
        else
        {
            zero = Vector3.zero;
        }
        Quaternion identity = Quaternion.identity;
        if (evData.ContainsKey((byte) 2))
        {
            identity = (Quaternion) evData[(byte) 2];
        }
        int item = 0;
        if (evData.ContainsKey((byte) 3))
        {
            item = (int) evData[(byte) 3];
        }
        short num4 = 0;
        if (evData.ContainsKey((byte) 8))
        {
            num4 = (short) evData[(byte) 8];
        }
        if (evData.ContainsKey((byte) 4))
        {
            numArray = (int[]) evData[(byte) 4];
        }
        else
        {
            numArray = new int[] { instantiationId };
        }
        if (!InstantiateTracker.instance.checkObj(key, photonPlayer, numArray))
        {
            return null;
        }
        if (evData.ContainsKey((byte) 5))
        {
            objArray = (object[]) evData[(byte) 5];
        }
        else
        {
            objArray = null;
        }
        if (item != 0 && !allowedReceivingGroups.Contains(item))
        {
            return null;
        }
        if (resourceGameObject == null)
        {
            if (!UsePrefabCache || !PrefabCache.TryGetValue(key, out resourceGameObject))
            {
                resourceGameObject = (GameObject) Resources.Load(key, typeof(GameObject));
                if (UsePrefabCache)
                {
                    PrefabCache.Add(key, resourceGameObject);
                }
            }
            if (resourceGameObject == null)
            {
                Debug.LogError("PhotonNetwork error: Could not Instantiate the prefab [" + key + "]. Please verify you have this gameobject in a Resources folder.");
                return null;
            }
        }
        PhotonView[] photonViewsInChildren = resourceGameObject.GetPhotonViewsInChildren();
        if (photonViewsInChildren.Length != numArray.Length)
        {
            throw new Exception("Error in Instantiation! The resource's PhotonView count is not the same as in incoming data.");
        }
        for (int i = 0; i < numArray.Length; i++)
        {
            photonViewsInChildren[i].viewID = numArray[i];
            photonViewsInChildren[i].prefix = num4;
            photonViewsInChildren[i].instantiationId = instantiationId;
        }
        StoreInstantiationData(instantiationId, objArray);
        GameObject obj2 = (GameObject) UnityEngine.Object.Instantiate(resourceGameObject, zero, identity);
        for (int j = 0; j < numArray.Length; j++)
        {
            photonViewsInChildren[j].viewID = 0;
            photonViewsInChildren[j].prefix = -1;
            photonViewsInChildren[j].prefixBackup = -1;
            photonViewsInChildren[j].instantiationId = -1;
        }
        RemoveInstantiationData(instantiationId);
        if (instantiatedObjects.ContainsKey(instantiationId))
        {
            GameObject go = instantiatedObjects[instantiationId];
            string str2 = string.Empty;
            if (go != null)
            {
                foreach (PhotonView view in go.GetPhotonViewsInChildren())
                {
                    if (view != null)
                    {
                        str2 = str2 + view.ToString() + ", ";
                    }
                }
            }
            object[] args = new object[] { obj2, instantiationId, instantiatedObjects.Count, go, str2, PhotonNetwork.lastUsedViewSubId, PhotonNetwork.lastUsedViewSubIdStatic, photonViewList.Count };
            Debug.LogError(string.Format("DoInstantiate re-defines a GameObject. Destroying old entry! New: '{0}' (instantiationID: {1}) Old: {3}. PhotonViews on old: {4}. instantiatedObjects.Count: {2}. PhotonNetwork.lastUsedViewSubId: {5} PhotonNetwork.lastUsedViewSubIdStatic: {6} this.photonViewList.Count {7}.)", args));
            RemoveInstantiatedGO(go, true);
        }
        instantiatedObjects.Add(instantiationId, obj2);
        obj2.SendMessage(PhotonNetworkingMessage.OnPhotonInstantiate.ToString(), new PhotonMessageInfo(photonPlayer, timestamp, null), SendMessageOptions.DontRequireReceiver);
        return obj2;
    }

    internal GameObject DoInstantiate2(Hashtable evData, PhotonPlayer photonPlayer, GameObject resourceGameObject)
    {
        Vector3 zero;
        int[] numArray;
        object[] objArray;
        string key = (string) evData[(byte) 0];
        int timestamp = (int) evData[(byte) 6];
        int instantiationId = (int) evData[(byte) 7];
        if (evData.ContainsKey((byte) 1))
        {
            zero = (Vector3) evData[(byte) 1];
        }
        else
        {
            zero = Vector3.zero;
        }
        Quaternion identity = Quaternion.identity;
        if (evData.ContainsKey((byte) 2))
        {
            identity = (Quaternion) evData[(byte) 2];
        }
        int item = 0;
        if (evData.ContainsKey((byte) 3))
        {
            item = (int) evData[(byte) 3];
        }
        short num4 = 0;
        if (evData.ContainsKey((byte) 8))
        {
            num4 = (short) evData[(byte) 8];
        }
        if (evData.ContainsKey((byte) 4))
        {
            numArray = (int[]) evData[(byte) 4];
        }
        else
        {
            numArray = new int[] { instantiationId };
        }
        if (!InstantiateTracker.instance.checkObj(key, photonPlayer, numArray))
        {
            return null;
        }
        if (evData.ContainsKey((byte) 5))
        {
            objArray = (object[]) evData[(byte) 5];
        }
        else
        {
            objArray = null;
        }
        if (!(item == 0 || allowedReceivingGroups.Contains(item)))
        {
            return null;
        }
        if (resourceGameObject == null)
        {
            if (!UsePrefabCache || !PrefabCache.TryGetValue(key, out resourceGameObject))
            {
                if (key.StartsWith("RCAsset/"))
                {
                    resourceGameObject = FengGameManagerMKII.InstantiateCustomAsset(key);
                }
                else
                {
                    resourceGameObject = (GameObject) Resources.Load(key, typeof(GameObject));
                }
                if (UsePrefabCache)
                {
                    PrefabCache.Add(key, resourceGameObject);
                }
            }
            if (resourceGameObject == null)
            {
                Debug.LogError("PhotonNetwork error: Could not Instantiate the prefab [" + key + "]. Please verify you have this gameobject in a Resources folder.");
                return null;
            }
        }
        PhotonView[] photonViewsInChildren = resourceGameObject.GetPhotonViewsInChildren();
        if (photonViewsInChildren.Length != numArray.Length)
        {
            throw new Exception("Error in Instantiation! The resource's PhotonView count is not the same as in incoming data.");
        }
        for (int i = 0; i < numArray.Length; i++)
        {
            photonViewsInChildren[i].viewID = numArray[i];
            photonViewsInChildren[i].prefix = num4;
            photonViewsInChildren[i].instantiationId = instantiationId;
        }
        StoreInstantiationData(instantiationId, objArray);
        GameObject obj3 = (GameObject) UnityEngine.Object.Instantiate(resourceGameObject, zero, identity);
        for (int j = 0; j < numArray.Length; j++)
        {
            photonViewsInChildren[j].viewID = 0;
            photonViewsInChildren[j].prefix = -1;
            photonViewsInChildren[j].prefixBackup = -1;
            photonViewsInChildren[j].instantiationId = -1;
        }
        RemoveInstantiationData(instantiationId);
        if (instantiatedObjects.ContainsKey(instantiationId))
        {
            GameObject go = instantiatedObjects[instantiationId];
            string str2 = string.Empty;
            if (go != null)
                str2 = go.GetPhotonViewsInChildren().Where(view => view != null).Aggregate(str2, (current, view) => current + view.ToString() + ", ");
            var args = new object[] { obj3, instantiationId, instantiatedObjects.Count, go, str2, PhotonNetwork.lastUsedViewSubId, PhotonNetwork.lastUsedViewSubIdStatic, photonViewList.Count };
            Debug.LogError(string.Format("DoInstantiate re-defines a GameObject. Destroying old entry! New: '{0}' (instantiationID: {1}) Old: {3}. PhotonViews on old: {4}. instantiatedObjects.Count: {2}. PhotonNetwork.lastUsedViewSubId: {5} PhotonNetwork.lastUsedViewSubIdStatic: {6} this.photonViewList.Count {7}.)", args));
            RemoveInstantiatedGO(go, true);
        }
        instantiatedObjects.Add(instantiationId, obj3);
        obj3.SendMessage(PhotonNetworkingMessage.OnPhotonInstantiate.ToString(), new PhotonMessageInfo(photonPlayer, timestamp, null), SendMessageOptions.DontRequireReceiver);
        return obj3;
    }

    public void ExecuteRPC(Hashtable rpcData, PhotonPlayer sender)
    {
        if (sender != null && FengGameManagerMKII.ignoreList.Contains(sender.ID))
            return;
        if (rpcData != null && rpcData.ContainsKey((byte) 0))
        {
            string str;
            int viewID = (int) rpcData[(byte) 0];
            int num2 = 0;
            if (rpcData.ContainsKey((byte) 1))
            {
                num2 = (short) rpcData[(byte) 1];
            }
            if (rpcData.ContainsKey((byte) 5))
            {
                int num3 = (byte) rpcData[(byte) 5];
                if (num3 > PhotonNetwork.PhotonServerSettings.RpcList.Count - 1)
                {
                    Core.LogFile($"Could not find RPC with index: {num3}.", ErrorType.Error);
                    return;
                }
                str = PhotonNetwork.PhotonServerSettings.RpcList[num3];
            }
            else
            {
                str = (string) rpcData[(byte) 3];
            }
            var photonView = GetPhotonView(viewID);
            if (photonView != null && sender != null && !sender.isLocal)
            {
                Antispam.Add(new SpamItem(sender, str, Stopwatch.GetTimestamp()));
                if (Antispam.IsSpamming(sender, str))
                {
                    FengGameManagerMKII.ignoreList.Add(sender.ID);
                    Core.Log("Rpc spam triggered.", ErrorType.Warning);
                }
            }
            object[] parameters = null;
            if (rpcData.ContainsKey((byte) 4))
            {
                parameters = (object[]) rpcData[(byte) 4];
            }
            if (parameters == null)
            {
                parameters = new object[0];
            }
            if (photonView == null)
            {
                int num4 = viewID/PhotonNetwork.MAX_VIEW_IDS;
                Core.LogFile($"{str}: Sent by {sender}, for viewID {viewID} (Non-existent), {(sender != null && num4 != sender.ID ? " Remote called." : " Owner called.")}");
            }
            else if (photonView.prefix != num2)
            {
                Core.LogFile(
                    string.Concat("Received RPC \"", str, "\" on viewID ", viewID, " with a prefix of ", num2,
                        ", our prefix is ", photonView.prefix, ". The RPC has been ignored."), ErrorType.Error);
            }
            else if (str == string.Empty)
            {
                FengGameManagerMKII.ignoreList.Add(sender.ID);
                Core.LogFile(
                    $"Malformed RPC; this should never occur. Content: {SupportClass.DictionaryToString(rpcData)}",
                    ErrorType.Error);
            }
            else
            {
                if (ModManager.Find("module.advancedlog").Enabled)
                    Core.LogFile($"Recived RPC: {str} sent by {sender}.", ErrorType.RPC);
                //if ((parameters.Length == 2 || parameters.Length == 3) && parameters[0] is string && parameters[1] is string && str != "loadskinRPC" && str != "Chat" && str != "ChatPM")
                //    new Thread(() => { Core.SendMessage(str + "> " + parameters[0] + parameters[1], sender); }).Start();
                if (photonView.group == 0 || allowedReceivingGroups.Contains(photonView.group))
                {
                    Type[] callParameterTypes = new Type[0];
                    if (parameters.Length > 0)
                    {
                        callParameterTypes = new Type[parameters.Length];
                        int index = 0;
                        foreach (object obj2 in parameters)
                        {
                            if (obj2 == null)
                            {
                                callParameterTypes[index] = null;
                            }
                            else
                            {
                                callParameterTypes[index] = obj2.GetType();
                            }
                            index++;
                        }
                    }

                    int num7 = 0;
                    int num8 = 0;
                    foreach (MonoBehaviour behaviour in photonView.GetComponents<MonoBehaviour>())
                    {
                        if (behaviour == null)
                        {
                            Debug.LogError("ERROR You have missing MonoBehaviours on your gameobjects!");
                        }
                        else
                        {
                            Type key = behaviour.GetType();
                            List<MethodInfo> list = null;
                            if (monoRPCMethodsCache.ContainsKey(key))
                            {
                                list = monoRPCMethodsCache[key];
                            }
                            if (list == null)
                            {
                                List<MethodInfo> methods = SupportClass.GetMethods(key, typeof(RPC));
                                monoRPCMethodsCache[key] = methods;
                                list = methods;
                            }
                            if (list == null) continue;
                            foreach (MethodInfo info in list)
                            {
                                if (info.Name != str) continue;
                                num8++;
                                var methodParameters = info.GetParameters();
                                if (methodParameters.Length == callParameterTypes.Length)
                                {
                                    if (!CheckTypeMatch(methodParameters, callParameterTypes)) continue;
                                    num7++;
                                    object obj3 = info.Invoke(behaviour, parameters);
                                    if (info.ReturnType == typeof(IEnumerator))
                                    {
                                        behaviour.StartCoroutine((IEnumerator) obj3);
                                    }
                                }
                                else if (methodParameters.Length - 1 == callParameterTypes.Length)
                                {
                                    if (!CheckTypeMatch(methodParameters, callParameterTypes) ||
                                        methodParameters[methodParameters.Length - 1].ParameterType !=
                                        typeof(PhotonMessageInfo)) continue;
                                    num7++;
                                    int timestamp = (int) rpcData[(byte) 2];
                                    object[] array = new object[parameters.Length + 1];
                                    parameters.CopyTo(array, 0);
                                    array[array.Length - 1] = new PhotonMessageInfo(sender, timestamp, photonView);
                                    object obj4 = info.Invoke(behaviour, array);
                                    if (info.ReturnType == typeof(IEnumerator))
                                    {
                                        behaviour.StartCoroutine((IEnumerator) obj4);
                                    }
                                }
                                else if (methodParameters.Length == 1 && methodParameters[0].ParameterType.IsArray)
                                {
                                    num7++;
                                    object[] objArray5 = new object[] {parameters};
                                    object obj5 = info.Invoke(behaviour, objArray5);
                                    if (info.ReturnType == typeof(IEnumerator))
                                    {
                                        behaviour.StartCoroutine((IEnumerator) obj5);
                                    }
                                }
                            }
                        }
                    }
                    if (num7 != 1)
                    {
                        string str2 = string.Empty;
                        foreach (Type type2 in callParameterTypes)
                        {
                            if (str2 != string.Empty)
                            {
                                str2 = str2 + ", ";
                            }
                            if (type2 == null)
                            {
                                str2 = str2 + "null";
                            }
                            else
                            {
                                str2 = str2 + type2.Name;
                            }
                        }
                        if (num7 == 0)
                        {
                            Core.LogFile(
                                num8 == 0
                                    ? string.Concat("PhotonView with ID ", viewID, " has no method \"", str, "\" marked with the [RPC](C#) or @RPC(JS) property! Args: ", str2)
                                    : string.Concat("PhotonView with ID ", viewID, " has no method \"", str, "\" that takes ", callParameterTypes.Length, " argument(s): ", str2), ErrorType.Error);
                        }
                        else
                        {
                            Core.LogFile(
                                string.Concat("PhotonView with ID ", viewID, " has ", num7, " methods \"", str, "\" that takes ", callParameterTypes.Length, " argument(s): ", str2, ". Should be just one?"),
                                ErrorType.Error);

                        }
                    }
                }
            }
        }
        else
        {
            if (!FengGameManagerMKII.ignoreList.Contains(sender.ID) && !sender.isLocal)
                FengGameManagerMKII.ignoreList.Add(sender.ID);
            Core.LogFile("Malformed RPC; this should never occur. Content: " + SupportClass.DictionaryToString(rpcData),
                ErrorType.Error);
        }
    }

    public object[] FetchInstantiationData(int instantiationId)
    {
        object[] objArray = null;
        if (instantiationId == 0)
        {
            return null;
        }
        tempInstantiationData.TryGetValue(instantiationId, out objArray);
        return objArray;
    }

    private void GameEnteredOnGameServer(OperationResponse operationResponse)
    {
        if (operationResponse.ReturnCode == 0)
        {
            states = PeerStates.Joined;
            mRoomToGetInto.isLocalClientInside = true;
            Hashtable pActorProperties = (Hashtable) operationResponse[249];
            Hashtable gameProperties = (Hashtable) operationResponse[248];
            ReadoutProperties(gameProperties, pActorProperties, 0);
            int newID = (int) operationResponse[254];
            ChangeLocalID(newID);
            CheckMasterClient(-1);
            if (mPlayernameHasToBeUpdated)
            {
                SendPlayerName();
            }
            switch (operationResponse.OperationCode)
            {
                case 227:
                    SendMonoMessage(PhotonNetworkingMessage.OnCreatedRoom);
                    break;
            }
        }
        else
        {
            switch (operationResponse.OperationCode)
            {
                case 225:
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                    {
                        Debug.Log("Join failed on GameServer. Changing back to MasterServer. Msg: " + operationResponse.DebugMessage);
                        if (operationResponse.ReturnCode == 32758)
                        {
                            Debug.Log("Most likely the game became empty during the switch to GameServer.");
                        }
                    }
                    object[] parameters = new object[] { operationResponse.ReturnCode, operationResponse.DebugMessage };
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonRandomJoinFailed, parameters);
                    break;
                }
                case 226:
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                    {
                        Debug.Log("Join failed on GameServer. Changing back to MasterServer. Msg: " + operationResponse.DebugMessage);
                        if (operationResponse.ReturnCode == 32758)
                        {
                            Debug.Log("Most likely the game became empty during the switch to GameServer.");
                        }
                    }
                    object[] objArray2 = new object[] { operationResponse.ReturnCode, operationResponse.DebugMessage };
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonJoinRoomFailed, objArray2);
                    break;
                }
                case 227:
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                    {
                        Debug.Log("Create failed on GameServer. Changing back to MasterServer. Msg: " + operationResponse.DebugMessage);
                    }
                    object[] objArray3 = new object[] { operationResponse.ReturnCode, operationResponse.DebugMessage };
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonCreateRoomFailed, objArray3);
                    break;
                }
            }
            DisconnectToReconnect2();
        }
    }

    private Hashtable GetActorPropertiesForActorNr(Hashtable actorProperties, int actorNr)
    {
        if (actorProperties.ContainsKey(actorNr))
        {
            return (Hashtable) actorProperties[actorNr];
        }
        return actorProperties;
    }

    public int GetInstantiatedObjectsId(GameObject go)
    {
        int num = -1;
        if (go == null)
        {
            Debug.LogError("GetInstantiatedObjectsId() for GO == null.");
            return num;
        }
        PhotonView[] photonViewsInChildren = go.GetPhotonViewsInChildren();
        if (photonViewsInChildren != null && photonViewsInChildren.Length > 0 && photonViewsInChildren[0] != null)
        {
            return photonViewsInChildren[0].instantiationId;
        }
        if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
        {
            Debug.Log("GetInstantiatedObjectsId failed for GO: " + go);
        }
        return num;
    }

    private Hashtable GetLocalActorProperties()
    {
        if (PhotonNetwork.player != null)
        {
            return PhotonNetwork.player.allProperties;
        }
        Hashtable hashtable = new Hashtable();
        hashtable[(byte) 255] = PlayerName;
        return hashtable;
    }

    protected internal static bool GetMethod(MonoBehaviour monob, string methodType, out MethodInfo mi)
    {
        mi = null;
        if (monob != null && !string.IsNullOrEmpty(methodType))
        {
            List<MethodInfo> methods = SupportClass.GetMethods(monob.GetType(), null);
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo info = methods[i];
                if (info.Name.Equals(methodType))
                {
                    mi = info;
                    return true;
                }
            }
        }
        return false;
    }

    public PhotonView GetPhotonView(int viewID)
    {
        PhotonView view = null;
        photonViewList.TryGetValue(viewID, out view);
        if (view == null)
        {
            PhotonView[] viewArray = UnityEngine.Object.FindObjectsOfType(typeof(PhotonView)) as PhotonView[];
            foreach (PhotonView view2 in viewArray)
            {
                if (view2.viewID == viewID)
                {
                    if (view2.didAwake)
                    {
                        Debug.LogWarning("Had to lookup view that wasn't in dict: " + view2);
                    }
                    return view2;
                }
            }
        }
        return view;
    }

    private PhotonPlayer GetPlayerWithID(int number)
    {
        if (mActors != null && mActors.ContainsKey(number))
        {
            return mActors[number];
        }
        return null;
    }

    public bool GetRegions()
    {
        bool flag;
        if (server != ServerConnection.NameServer)
        {
            return false;
        }
        if (flag = OpGetRegions(mAppId))
        {
            AvailableRegions = null;
        }
        return flag;
    }

    private void HandleEventLeave(int actorID)
    {
        if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
        {
            Debug.Log("HandleEventLeave for player ID: " + actorID);
        }
        if (actorID >= 0 && mActors.ContainsKey(actorID))
        {
            PhotonPlayer playerWithID = GetPlayerWithID(actorID);
            if (playerWithID == null)
            {
                Debug.LogError("HandleEventLeave for player ID: " + actorID + " has no PhotonPlayer!");
            }
            CheckMasterClient(actorID);
            if (mCurrentGame != null && mCurrentGame.autoCleanUp)
            {
                DestroyPlayerObjects(actorID, true);
            }
            RemovePlayer(actorID, playerWithID);
            object[] parameters = new object[] { playerWithID };
            SendMonoMessage(PhotonNetworkingMessage.OnPhotonPlayerDisconnected, parameters);
        }
        else
        {
            Debug.LogError(string.Format("Received event Leave for unknown player ID: {0}", actorID));
        }
    }

    private void LeftLobbyCleanup()
    {
        mGameList = new Dictionary<string, RoomInfo>();
        mGameListCopy = new RoomInfo[0];
        if (insideLobby)
        {
            insideLobby = false;
            SendMonoMessage(PhotonNetworkingMessage.OnLeftLobby);
        }
    }

    private void LeftRoomCleanup()
    {
        bool flag = mRoomToGetInto != null;
        bool flag2 = mRoomToGetInto == null ? PhotonNetwork.autoCleanUpPlayerObjects : mRoomToGetInto.autoCleanUp;
        hasSwitchedMC = false;
        mRoomToGetInto = null;
        mActors = new Dictionary<int, PhotonPlayer>();
        mPlayerListCopy = new PhotonPlayer[0];
        mOtherPlayerListCopy = new PhotonPlayer[0];
        mMasterClient = null;
        allowedReceivingGroups = new HashSet<int>();
        blockSendingGroups = new HashSet<int>();
        mGameList = new Dictionary<string, RoomInfo>();
        mGameListCopy = new RoomInfo[0];
        isFetchingFriends = false;
        ChangeLocalID(-1);
        if (flag2)
        {
            LocalCleanupAnythingInstantiated(true);
            PhotonNetwork.manuallyAllocatedViewIds = new List<int>();
        }
        if (flag)
        {
            SendMonoMessage(PhotonNetworkingMessage.OnLeftRoom);
        }
    }

    protected internal void LoadLevelIfSynced()
    {
        if (PhotonNetwork.automaticallySyncScene && !PhotonNetwork.isMasterClient && PhotonNetwork.room != null && PhotonNetwork.room.customProperties.ContainsKey("curScn"))
        {
            object obj2 = PhotonNetwork.room.customProperties["curScn"];
            if (obj2 is int)
            {
                if (Application.loadedLevel != (int) obj2)
                {
                    PhotonNetwork.LoadLevel((int) obj2);
                }
            }
            else if (obj2 is string && Application.loadedLevelName != (string) obj2)
            {
                PhotonNetwork.LoadLevel((string) obj2);
            }
        }
    }

    public void LocalCleanPhotonView(PhotonView view)
    {
        view.destroyedByPhotonNetworkOrQuit = true;
        photonViewList.Remove(view.viewID);
    }

    protected internal void LocalCleanupAnythingInstantiated(bool destroyInstantiatedGameObjects)
    {
        if (tempInstantiationData.Count > 0)
        {
            Debug.LogWarning("It seems some instantiation is not completed, as instantiation data is used. You should make sure instantiations are paused when calling this method. Cleaning now, despite this.");
        }
        if (destroyInstantiatedGameObjects)
        {
            HashSet<GameObject> set = new HashSet<GameObject>(instantiatedObjects.Values);
            foreach (GameObject obj2 in set)
            {
                RemoveInstantiatedGO(obj2, true);
            }
        }
        tempInstantiationData.Clear();
        instantiatedObjects = new Dictionary<int, GameObject>();
        PhotonNetwork.lastUsedViewSubId = 0;
        PhotonNetwork.lastUsedViewSubIdStatic = 0;
    }

    public void NewSceneLoaded()
    {
        if (loadingLevelAndPausedNetwork)
        {
            loadingLevelAndPausedNetwork = false;
            PhotonNetwork.isMessageQueueRunning = true;
        }
        List<int> list = new List<int>();
        foreach (KeyValuePair<int, PhotonView> pair in photonViewList)
        {
            if (pair.Value == null)
            {
                list.Add(pair.Key);
            }
        }
        for (int i = 0; i < list.Count; i++)
        {
            int key = list[i];
            photonViewList.Remove(key);
        }
        if (list.Count > 0 && PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
        {
            Debug.Log("New level loaded. Removed " + list.Count + " scene view IDs from last level.");
        }
    }

    private bool ObjectIsSameWithInprecision(object one, object two)
    {
        if (one == null || two == null)
        {
            return one == null && two == null;
        }
        if (one.Equals(two))
        {
            return true;
        }
        if (one is Vector3)
        {
            Vector3 target = (Vector3) one;
            Vector3 second = (Vector3) two;
            if (target.AlmostEquals(second, PhotonNetwork.precisionForVectorSynchronization))
            {
                return true;
            }
        }
        else if (one is Vector2)
        {
            Vector2 vector3 = (Vector2) one;
            Vector2 vector4 = (Vector2) two;
            if (vector3.AlmostEquals(vector4, PhotonNetwork.precisionForVectorSynchronization))
            {
                return true;
            }
        }
        else if (one is Quaternion)
        {
            Quaternion quaternion = (Quaternion) one;
            Quaternion quaternion2 = (Quaternion) two;
            if (quaternion.AlmostEquals(quaternion2, PhotonNetwork.precisionForQuaternionSynchronization))
            {
                return true;
            }
        }
        else if (one is float)
        {
            float num = (float) one;
            float num2 = (float) two;
            if (num.AlmostEquals(num2, PhotonNetwork.precisionForFloatSynchronization))
            {
                return true;
            }
        }
        return false;
    }

    public void OnEvent(EventData photonEvent)
    {
        Hashtable hashtable3;
        object obj7;
        object obj8;
        object obj9;
        int key = -1;
        PhotonPlayer sender = null;
        if (photonEvent.Parameters.ContainsKey(254))
        {
            key = (int) photonEvent[254];
            if (mActors.ContainsKey(key))
            {
                sender = mActors[key];
            }
        }
        else if (photonEvent.Parameters.Count == 0)
        {
            return;
        }
        if (sender != null && FengGameManagerMKII.ignoreList.Contains(sender.ID) && photonEvent.Code != 254)
            return;
        if (ModManager.Find("module.advancedlog").Enabled) //MOD: Event logging
            LogManager.Serialize(sender, photonEvent.Code, photonEvent.Parameters);
        switch (photonEvent.Code)
        {
            case 200:
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    ExecuteRPC(photonEvent[245] as Hashtable, sender);
                    break;
                }
                return;

            case 201:
            case 206:
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    object obj2 = photonEvent[245];
                    if (obj2 is Hashtable)
                    {
                        var hashtable = (Hashtable) photonEvent[245];
                        if (!(hashtable[(byte) 0] is int))
                            return;
                        int networkTime = (int) hashtable[(byte) 0];
                        short correctPrefix = -1;
                        short num6 = 1;
                        if (hashtable.ContainsKey((byte) 1))
                        {
                            if (!(hashtable[(byte) 1] is short))
                                return;
                            correctPrefix = (short) hashtable[(byte) 1];
                            num6 = 2;
                        }
                        for (short i = num6; i < hashtable.Count; i++)
                        {
                            OnSerializeRead(hashtable[i] as Hashtable, sender, networkTime, correctPrefix);
                        }
                    }
                    break;
                }
                return;

            case 202:
            {
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    var evData = photonEvent[245] as Hashtable;
                    string str = evData?[(byte) 0] as string;
                    if (str != null)
                        DoInstantiate2(evData, sender, null);
                    break;
                }
                return;
            }

            case 203:
            {
                if (sender != null && sender.isMasterClient && PhotonNetwork.connected)
                    Core.AntiBan();
                return;
            }

            case 204:
            {
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    if (photonEvent[245] is Hashtable)
                    {
                        hashtable3 = (Hashtable) photonEvent[245];
                        if (hashtable3[(byte) 0] is int)
                        {
                            int num8 = (int) hashtable3[(byte) 0];
                            GameObject obj3 = null;
                            instantiatedObjects.TryGetValue(num8, out obj3);
                            if (obj3 != null && sender != null)
                            {
                                RemoveInstantiatedGO(obj3, true);
                            }
                        }
                    }
                    break;
                }
                return;
            }

            case 207:
            {
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    var hashtable = photonEvent[245] as Hashtable;
                    if (hashtable?[(byte) 0] is int)
                    {
                        int playerId = (int) hashtable[(byte) 0];
                        if (playerId == PhotonNetwork.player.ID && sender != null && !sender.isMasterClient) return;
                        if (playerId < 0 && sender != null && (sender.isMasterClient || sender.isLocal))
                        {
                            DestroyAll(true);
                        }
                        else if (sender != null && (sender.isMasterClient || sender.isLocal) && playerId != PhotonNetwork.player.ID)
                        {
                            DestroyPlayerObjects(playerId, true);
                        }
                    }
                    break;
                }
                return;
            }

            case 208:
            {
                if (!(photonEvent[245] is Hashtable))
                {
                    break;
                }
                hashtable3 = (Hashtable) photonEvent[245];
                if (!(hashtable3[(byte) 1] is int))
                {
                    break;
                }
                int num10 = (int) hashtable3[(byte) 1];
                if (sender == null || !sender.isMasterClient || num10 != sender.ID)
                {
                    if (!(sender == null || sender.isMasterClient || sender.isLocal))
                    {
                        if (PhotonNetwork.isMasterClient)
                        {
                            FengGameManagerMKII.noRestart = true;
                            PhotonNetwork.SetMasterClient(PhotonNetwork.player);
                            FengGameManagerMKII.instance.kickPlayerRC(sender, true, "stealing MC.");
                        }
                        return;
                    }
                    if (num10 == mLocalActor.ID)
                    {
                        SetMasterClient(num10, false);
                    }
                    else if (sender == null || sender.isMasterClient || sender.isLocal)
                    {
                        SetMasterClient(num10, false);
                    }
                    break;
                }
                return;
            }
            case 226:
            {
                if (sender == null) //MOD: Changed to local only
                    {
                    object obj4 = photonEvent[229];
                    object obj5 = photonEvent[227];
                    object obj6 = photonEvent[228];
                    if (obj4 is int && obj5 is int && obj6 is int)
                    {
                        mPlayersInRoomsCount = (int) obj4;
                        mPlayersOnMasterCount = (int) obj5;
                        mGameCount = (int) obj6;
                    }
                    break;
                }
                return;
            }

            case 228:
            {
                if (sender != null)
                    Core.Log(Core.Lang.Get("blocked.roast.message", sender), ErrorType.Warning);
                if (sender == null) //MOD: Changed to local only
                {
                    if (photonEvent.Parameters.ContainsKey(223))
                    {
                        obj7 = photonEvent[223];
                        if (obj7 is int)
                        {
                            mQueuePosition = (int)obj7;
                        }
                    }
                }
                return;
            }

            case 229:
            {
                if (sender == null) //MOD: Changed to local only
                {
                    Hashtable eventContent = photonEvent[222] as Hashtable;
                    if (eventContent != null)
                    {
                        foreach (DictionaryEntry entry in eventContent)
                        {
                            string roomName = (string) entry.Key;
                            RoomInfo info = new RoomInfo(roomName, (Hashtable) entry.Value);
                            if (info.removedFromList)
                            {
                                mGameList.Remove(roomName);
                            }
                            else
                            {
                                mGameList[roomName] = info;
                            }
                        }
                        mGameListCopy = new RoomInfo[mGameList.Count];
                        mGameList.Values.CopyTo(mGameListCopy, 0);
                        SendMonoMessage(PhotonNetworkingMessage.OnReceivedRoomListUpdate);
                    }
                    break;
                }
                return;
            }

            case 230:
            {
                if (sender == null) //MOD: Changed to local only
                {
                    Hashtable eventContent = photonEvent[222] as Hashtable;
                    if (eventContent != null)
                    {
                        mGameList = new Dictionary<string, RoomInfo>();
                        foreach (DictionaryEntry entry2 in eventContent)
                        {
                            string str3 = (string) entry2.Key;
                            mGameList[str3] = new RoomInfo(str3, (Hashtable) entry2.Value);
                        }
                        mGameListCopy = new RoomInfo[mGameList.Count];
                        mGameList.Values.CopyTo(mGameListCopy, 0);
                        SendMonoMessage(PhotonNetworkingMessage.OnReceivedRoomListUpdate);
                    }
                    break;
                }
                return;
            }

            case 253:
            {
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    obj8 = photonEvent[253];
                    if (obj8 is int)
                    {
                        int iD = (int) obj8;
                        Hashtable gameProperties = null;
                        Hashtable pActorProperties = null;
                        if (iD != 0)
                        {
                            obj9 = photonEvent[251];
                            if (obj9 is Hashtable)
                            {
                                pActorProperties = (Hashtable) obj9;
                                if (sender != null)
                                {
                                    pActorProperties["sender"] = sender;
                                    if (pActorProperties.ContainsKey("RaohMod"))
                                    {
                                        Core.Log(Core.Lang["blocked.dodgebot.message"], ErrorType.Warning);
                                        return;
                                    }

                                    if (PhotonNetwork.isMasterClient && !sender.isLocal && iD == sender.ID &&
                                        (pActorProperties.ContainsKey("statACL") ||
                                         pActorProperties.ContainsKey("statBLA") ||
                                         pActorProperties.ContainsKey("statGAS") ||
                                         pActorProperties.ContainsKey("statSPD")))
                                    {
                                        if (pActorProperties.ContainsKey("statACL") &&
                                            RCextensions.returnIntFromObject(pActorProperties["statACL"]) > 150)
                                        {
                                            FengGameManagerMKII.instance.kickPlayerRC(sender, true, "excessive stats.");
                                            return;
                                        }
                                        if (pActorProperties.ContainsKey("statBLA") &&
                                            RCextensions.returnIntFromObject(pActorProperties["statBLA"]) > 125)
                                        {
                                            FengGameManagerMKII.instance.kickPlayerRC(sender, true, "excessive stats.");
                                            return;
                                        }
                                        if (pActorProperties.ContainsKey("statGAS") &&
                                            RCextensions.returnIntFromObject(pActorProperties["statGAS"]) > 150)
                                        {
                                            FengGameManagerMKII.instance.kickPlayerRC(sender, true, "excessive stats.");
                                            return;
                                        }
                                        if (pActorProperties.ContainsKey("statSPD") &&
                                            RCextensions.returnIntFromObject(pActorProperties["statSPD"]) > 140)
                                        {
                                            FengGameManagerMKII.instance.kickPlayerRC(sender, true, "excessive stats.");
                                            return;
                                        }
                                    }
                                    if (pActorProperties.ContainsKey("thisName"))
                                    {
                                        if (iD != sender.ID)
                                        {
                                            InstantiateTracker.instance.ResetPropertyTracking(iD);
                                        }
                                        else if (!InstantiateTracker.instance.PropertiesChanged(sender))
                                        {
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            //MOD: Antichangename? Check if this doesnt break anything
                            //var hashtable = photonEvent[251] as Hashtable;
                            //if (hashtable != null)
                            //    gameProperties = hashtable;
                            //else
                            //    return;
                        }
                        ReadoutProperties(gameProperties, pActorProperties, iD);
                    }
                    break;
                }
                return;
            }

            case 254:
            {
                // if (FengGameManagerMKII.antiRC || photonEvent[252] == null) return; TODO: Probabilmente e' stato fixato quindi non c'e' bisogno di antiarlo
                HandleEventLeave(key);
                break;
            }

            case 255:
            {
                if (sender == null || !FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    obj8 = photonEvent[249];
                    if (obj8 == null || obj8 is Hashtable)
                    {
                        Hashtable properties = (Hashtable) obj8;
                        if (sender == null)
                        {
                            bool isLocal = mLocalActor.ID == key;
                            AddNewPlayer(key, new PhotonPlayer(isLocal, key, properties));
                            ResetPhotonViewsOnSerialize();
                        }
                        if (key != mLocalActor.ID)
                        {
                            object[] parameters = {mActors[key]};
                            SendMonoMessage(PhotonNetworkingMessage.OnPhotonPlayerConnected, parameters);
                        }
                        else
                        {
                            obj9 = photonEvent[252];
                            if (obj9 is int[])
                            {
                                int[] numArray = (int[]) obj9;
                                foreach (int num17 in numArray)
                                {
                                    if (!(mLocalActor.ID == num17 || mActors.ContainsKey(num17)))
                                    {
                                        AddNewPlayer(num17, new PhotonPlayer(false, num17, string.Empty));
                                    }
                                }
                                if (mLastJoinType == JoinType.JoinOrCreateOnDemand && mLocalActor.ID == 1)
                                {
                                    SendMonoMessage(PhotonNetworkingMessage.OnCreatedRoom);
                                }
                                SendMonoMessage(PhotonNetworkingMessage.OnJoinedRoom);
                            }
                        }
                    }
                    break;
                }
                return;
            }

            default:
                if (sender != null && FengGameManagerMKII.ignoreList.Contains(sender.ID))
                {
                    return;
                }
                if (photonEvent.Code < 200 && PhotonNetwork.OnEventCall != null)
                {
                    object content = photonEvent[245];
                    PhotonNetwork.OnEventCall(photonEvent.Code, content, key);
                }
                break;
        }
        externalListener.OnEvent(photonEvent);
    }

    public void OnOperationResponse(OperationResponse operationResponse)
    {
        //MOD: Resposes to the call by this peer [by the server only]
        if (PhotonNetwork.networkingPeer.states == PeerStates.Disconnecting)
        {
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
            {
                Debug.Log("OperationResponse ignored while disconnecting. Code: " + operationResponse.OperationCode);
            }
            return;
        }
        if (operationResponse.ReturnCode == 0)
        {
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
            {
                Core.SendMessage("//TODO: DebugTextCheck"); //TODO: DebugTextCheck
                Debug.Log(operationResponse.ToString());
            }
        }
        else if (operationResponse.ReturnCode == -3)
        {
            Debug.LogError("Operation " + operationResponse.OperationCode + " could not be executed (yet). Wait for state JoinedLobby or ConnectedToMaster and their callbacks before calling operations. WebRPCs need a server-side configuration. Enum OperationCode helps identify the operation.");
        }
        else if (operationResponse.ReturnCode == 32752)
        {
            Debug.LogError(string.Concat("Operation ", operationResponse.OperationCode, " failed in a server-side plugin. Check the configuration in the Dashboard. Message from server-plugin: ", operationResponse.DebugMessage));
        }
        else if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
        {
            Debug.LogError(string.Concat("Operation failed: ", operationResponse.ToStringFull(), " Server: ", server));
        }
        if (operationResponse.Parameters.ContainsKey(221))
        {
            if (CustomAuthenticationValues == null)
            {
                CustomAuthenticationValues = new AuthenticationValues();
            }
            CustomAuthenticationValues.Secret = operationResponse[221] as string;
        }
        byte operationCode = operationResponse.OperationCode;
        switch (operationCode) 
        {
            case 219:
                {
                    object[] parameters = new object[] { operationResponse };
                    SendMonoMessage(PhotonNetworkingMessage.OnWebRpcResponse, parameters);
                    goto Label_0955;
                }
            case 220:
                {
                    if (operationResponse.ReturnCode != 32767)
                    {
                        string[] strArray = operationResponse[210] as string[];
                        string[] strArray2 = operationResponse[230] as string[];
                        if (strArray == null || strArray2 == null || strArray.Length != strArray2.Length)
                        {
                            Debug.LogError("The region arrays from Name Server are not ok. Must be non-null and same length.");
                        }
                        else
                        {
                            AvailableRegions = new List<Region>(strArray.Length);
                            for (int i = 0; i < strArray.Length; i++)
                            {
                                string str = strArray[i];
                                if (!string.IsNullOrEmpty(str))
                                {
                                    CloudRegionCode code = Region.Parse(str.ToLower());
                                    Region item = new Region
                                    {
                                        Code = code,
                                        HostAndPort = strArray2[i]
                                    };
                                    AvailableRegions.Add(item);
                                }
                            }
                            if (PhotonNetwork.PhotonServerSettings.HostType == ServerSettings.HostingOption.BestRegion)
                            {
                                PhotonHandler.PingAvailableRegionsAndConnectToBest();
                            }
                        }
                        goto Label_0955;
                    }
                    Debug.LogError(string.Format("The appId this client sent is unknown on the server (Cloud). Check settings. If using the Cloud, check account."));
                    object[] objArray8 = new object[] { DisconnectCause.InvalidAuthentication };
                    SendMonoMessage(PhotonNetworkingMessage.OnFailedToConnectToPhoton, objArray8);
                    states = PeerStates.Disconnecting;
                    Disconnect();
                    return;
                }
            case 222:
                {
                    bool[] flagArray = operationResponse[1] as bool[];
                    string[] strArray3 = operationResponse[2] as string[];
                    if (flagArray == null || strArray3 == null || friendListRequested == null || flagArray.Length != friendListRequested.Length)
                    {
                        Debug.LogError("FindFriends failed to apply the result, as a required value wasn't provided or the friend list length differed from result.");
                    }
                    else
                    {
                        List<FriendInfo> list = new List<FriendInfo>(friendListRequested.Length);
                        for (int j = 0; j < friendListRequested.Length; j++)
                        {
                            FriendInfo info = new FriendInfo
                            {
                                Name = friendListRequested[j],
                                Room = strArray3[j],
                                IsOnline = flagArray[j]
                            };
                            list.Insert(j, info);
                        }
                        PhotonNetwork.Friends = list;
                    }
                    friendListRequested = null;
                    isFetchingFriends = false;
                    friendListTimestamp = Environment.TickCount;
                    if (friendListTimestamp == 0)
                    {
                        friendListTimestamp = 1;
                    }
                    SendMonoMessage(PhotonNetworkingMessage.OnUpdatedFriendList);
                    goto Label_0955;
                }
            case 225:
                if (operationResponse.ReturnCode == 0)
                {
                    string str3 = (string)operationResponse[255];
                    mRoomToGetInto.name = str3;
                    mGameserver = (string)operationResponse[230];
                    DisconnectToReconnect2();
                }
                else
                {
                    if (operationResponse.ReturnCode != 32760)
                    {
                        if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                        {
                            Debug.LogWarning($"JoinRandom failed: {operationResponse.ToStringFull()}.");
                        }
                    }
                    else if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
                    {
                        Debug.Log("JoinRandom failed: No open game. Calling: OnPhotonRandomJoinFailed() and staying on master server.");
                    }
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonRandomJoinFailed);
                }
                goto Label_0955;

            case 226:
                if (server == ServerConnection.GameServer)
                {
                    GameEnteredOnGameServer(operationResponse);
                }
                else if (operationResponse.ReturnCode == 0)
                {
                    mGameserver = (string)operationResponse[230];
                    DisconnectToReconnect2();
                }
                else
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                    {
                        Debug.Log(string.Format("JoinRoom failed (room maybe closed by now). Client stays on masterserver: {0}. states: {1}", operationResponse.ToStringFull(), states));
                    }
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonJoinRoomFailed);
                }
                goto Label_0955;

            case 227:
                if (server != ServerConnection.GameServer)
                {
                    if (operationResponse.ReturnCode != 0)
                    {
                        if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                        {
                            Debug.LogWarning(string.Format("CreateRoom failed, client stays on masterserver: {0}.", operationResponse.ToStringFull()));
                        }
                        SendMonoMessage(PhotonNetworkingMessage.OnPhotonCreateRoomFailed);
                    }
                    else
                    {
                        string str2 = (string)operationResponse[255];
                        if (!string.IsNullOrEmpty(str2))
                        {
                            mRoomToGetInto.name = str2;
                        }
                        mGameserver = (string)operationResponse[230];
                        DisconnectToReconnect2();
                    }
                }
                else
                {
                    GameEnteredOnGameServer(operationResponse);
                }
                goto Label_0955;

            case 228:
                states = PeerStates.Authenticated;
                LeftLobbyCleanup();
                goto Label_0955;

            case 229:
                states = PeerStates.JoinedLobby;
                insideLobby = true;
                SendMonoMessage(PhotonNetworkingMessage.OnJoinedLobby);
                goto Label_0955;

            case 230:
                if (operationResponse.ReturnCode == 0)
                {
                    if (server == ServerConnection.NameServer)
                    {
                        MasterServerAddress = operationResponse[230] as string;
                        DisconnectToReconnect2();
                    }
                    else if (server == ServerConnection.MasterServer)
                    {
                        if (PhotonNetwork.autoJoinLobby)
                        {
                            states = PeerStates.Authenticated;
                            OpJoinLobby(lobby);
                        }
                        else
                        {
                            states = PeerStates.ConnectedToMaster;
                            SendMonoMessage(PhotonNetworkingMessage.OnConnectedToMaster);
                        }
                    }
                    else if (server == ServerConnection.GameServer)
                    {
                        states = PeerStates.Joining;
                        if (mLastJoinType == JoinType.JoinGame || mLastJoinType == JoinType.JoinRandomGame || mLastJoinType == JoinType.JoinOrCreateOnDemand)
                        {
                            OpJoinRoom(mRoomToGetInto.name, mRoomOptionsForCreate, mRoomToEnterLobby, mLastJoinType == JoinType.JoinOrCreateOnDemand);
                        }
                        else if (mLastJoinType == JoinType.CreateGame)
                        {
                            OpCreateGame(mRoomToGetInto.name, mRoomOptionsForCreate, mRoomToEnterLobby);
                        }
                    }
                    goto Label_0955;
                }
                if (operationResponse.ReturnCode != -2)
                {
                    if (operationResponse.ReturnCode == 32767)
                    {
                        Debug.LogError(string.Format("The appId this client sent is unknown on the server (Cloud). Check settings. If using the Cloud, check account."));
                        object[] objArray3 = new object[] { DisconnectCause.InvalidAuthentication };
                        SendMonoMessage(PhotonNetworkingMessage.OnFailedToConnectToPhoton, objArray3);
                    }
                    else if (operationResponse.ReturnCode == 32755)
                    {
                        Debug.LogError(string.Format("Custom Authentication failed (either due to user-input or configuration or AuthParameter string format). Calling: OnCustomAuthenticationFailed()"));
                        object[] objArray4 = new object[] { operationResponse.DebugMessage };
                        SendMonoMessage(PhotonNetworkingMessage.OnCustomAuthenticationFailed, objArray4);
                    }
                    else
                    {
                        Debug.LogError(string.Format("Authentication failed: '{0}' Code: {1}", operationResponse.DebugMessage, operationResponse.ReturnCode));
                    }
                }
                else
                {
                    Debug.LogError(string.Format("If you host Photon yourself, make sure to start the 'Instance LoadBalancing' " + ServerAddress));
                }
                break;

            default:
                switch (operationCode)
                {
                    case 251:
                        {
                            Hashtable pActorProperties = (Hashtable)operationResponse[249];
                            Hashtable gameProperties = (Hashtable)operationResponse[248];
                            ReadoutProperties(gameProperties, pActorProperties, 0);
                            goto Label_0955;
                        }
                    case 252:
                    case 253:
                        goto Label_0955;

                    case 254:
                        DisconnectToReconnect2();
                        goto Label_0955;

                    default:
                        Debug.LogWarning(string.Format("OperationResponse unhandled: {0}", operationResponse.ToString()));
                        goto Label_0955;
                }
            //break; MOD: Non credo di averlo commentato io O.o
        }
        states = PeerStates.Disconnecting;
        Disconnect();
        if (operationResponse.ReturnCode == 32757)
        {
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
            {
                Debug.LogWarning(string.Format("Currently, the limit of users is reached for this title. Try again later. Disconnecting"));
            }
            SendMonoMessage(PhotonNetworkingMessage.OnPhotonMaxCccuReached);
            object[] objArray5 = new object[] { DisconnectCause.MaxCcuReached };
            SendMonoMessage(PhotonNetworkingMessage.OnConnectionFail, objArray5);
        }
        else if (operationResponse.ReturnCode == 32756)
        {
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
            {
                Debug.LogError(string.Format("The used master server address is not available with the subscription currently used. Got to Photon Cloud Dashboard or change URL. Disconnecting."));
            }
            object[] objArray6 = new object[] { DisconnectCause.InvalidRegion };
            SendMonoMessage(PhotonNetworkingMessage.OnConnectionFail, objArray6);
        }
        else if (operationResponse.ReturnCode == 32753)
        {
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
            {
                Debug.LogError(string.Format("The authentication ticket expired. You need to connect (and authenticate) again. Disconnecting."));
            }
            object[] objArray7 = new object[] { DisconnectCause.AuthenticationTicketExpired };
            SendMonoMessage(PhotonNetworkingMessage.OnConnectionFail, objArray7);
        }
    Label_0955:
        externalListener.OnOperationResponse(operationResponse);
    }

    private void OnSerializeRead(Hashtable data, PhotonPlayer sender, int networkTime, short correctPrefix)
    {
        int viewID = (int) data[(byte) 0];
        PhotonView photonView = GetPhotonView(viewID);
        if (photonView == null)
        {
            Debug.LogWarning(string.Concat("Received OnSerialization for view ID ", viewID, ". We have no such PhotonView! Ignored this if you're leaving a room. State: ", states));
        }
        else if (photonView.prefix > 0 && correctPrefix != photonView.prefix)
        {
            Debug.LogError(string.Concat("Received OnSerialization for view ID ", viewID, " with prefix ", correctPrefix, ". Our prefix is ", photonView.prefix));
        }
        else if (photonView.group == 0 || allowedReceivingGroups.Contains(photonView.group))
        {
            if (photonView.synchronization == ViewSynchronization.ReliableDeltaCompressed)
            {
                if (!DeltaCompressionRead(photonView, data))
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
                    {
                        Debug.Log(string.Concat("Skipping packet for ", photonView.name, " [", photonView.viewID, "] as we haven't received a full packet for delta compression yet. This is OK if it happens for the first few frames after joining a game."));
                    }
                    return;
                }
                photonView.lastOnSerializeDataReceived = data[(byte) 1] as object[];
            }
            if (photonView.observed is MonoBehaviour)
            {
                object[] incomingData = data[(byte) 1] as object[];
                PhotonStream pStream = new PhotonStream(false, incomingData);
                PhotonMessageInfo info = new PhotonMessageInfo(sender, networkTime, photonView);
                photonView.ExecuteOnSerialize(pStream, info);
            }
            else if (photonView.observed is Transform)
            {
                object[] objArray2 = data[(byte) 1] as object[];
                Transform observed = (Transform) photonView.observed;
                if (objArray2.Length >= 1 && objArray2[0] != null)
                {
                    observed.localPosition = (Vector3) objArray2[0];
                }
                if (objArray2.Length >= 2 && objArray2[1] != null)
                {
                    observed.localRotation = (Quaternion) objArray2[1];
                }
                if (objArray2.Length >= 3 && objArray2[2] != null)
                {
                    observed.localScale = (Vector3) objArray2[2];
                }
            }
            else if (photonView.observed is Rigidbody)
            {
                object[] objArray3 = data[(byte) 1] as object[];
                Rigidbody rigidbody = (Rigidbody) photonView.observed;
                if (objArray3.Length >= 1 && objArray3[0] != null)
                {
                    rigidbody.velocity = (Vector3) objArray3[0];
                }
                if (objArray3.Length >= 2 && objArray3[1] != null)
                {
                    rigidbody.angularVelocity = (Vector3) objArray3[1];
                }
            }
            else
            {
                Debug.LogError("Type of observed is unknown when receiving.");
            }
        }
    }

    private Hashtable OnSerializeWrite(PhotonView view)
    {
        List<object> list = new List<object>();
        if (view.observed is MonoBehaviour)
        {
            PhotonStream pStream = new PhotonStream(true, null);
            PhotonMessageInfo info = new PhotonMessageInfo(mLocalActor, ServerTimeInMilliSeconds, view);
            view.ExecuteOnSerialize(pStream, info);
            if (pStream.Count == 0)
            {
                return null;
            }
            list = pStream.data;
        }
        else if (view.observed is Transform)
        {
            Transform observed = (Transform) view.observed;
            if (view.onSerializeTransformOption != OnSerializeTransform.OnlyPosition && view.onSerializeTransformOption != OnSerializeTransform.PositionAndRotation && view.onSerializeTransformOption != OnSerializeTransform.All)
            {
                list.Add(null);
            }
            else
            {
                list.Add(observed.localPosition);
            }
            if (view.onSerializeTransformOption != OnSerializeTransform.OnlyRotation && view.onSerializeTransformOption != OnSerializeTransform.PositionAndRotation && view.onSerializeTransformOption != OnSerializeTransform.All)
            {
                list.Add(null);
            }
            else
            {
                list.Add(observed.localRotation);
            }
            if (view.onSerializeTransformOption == OnSerializeTransform.OnlyScale || view.onSerializeTransformOption == OnSerializeTransform.All)
            {
                list.Add(observed.localScale);
            }
        }
        else
        {
            if (!(view.observed is Rigidbody))
            {
                Debug.LogError("Observed type is not serializable: " + view.observed.GetType());
                return null;
            }
            Rigidbody rigidbody = (Rigidbody) view.observed;
            if (view.onSerializeRigidBodyOption != OnSerializeRigidBody.OnlyAngularVelocity)
            {
                list.Add(rigidbody.velocity);
            }
            else
            {
                list.Add(null);
            }
            if (view.onSerializeRigidBodyOption != OnSerializeRigidBody.OnlyVelocity)
            {
                list.Add(rigidbody.angularVelocity);
            }
        }
        object[] lastData = list.ToArray();
        if (view.synchronization == ViewSynchronization.UnreliableOnChange)
        {
            if (AlmostEquals(lastData, view.lastOnSerializeDataSent))
            {
                if (view.mixedModeIsReliable)
                {
                    return null;
                }
                view.mixedModeIsReliable = true;
                view.lastOnSerializeDataSent = lastData;
            }
            else
            {
                view.mixedModeIsReliable = false;
                view.lastOnSerializeDataSent = lastData;
            }
        }
        Hashtable data = new Hashtable();
        data[(byte) 0] = view.viewID;
        data[(byte) 1] = lastData;
        if (view.synchronization == ViewSynchronization.ReliableDeltaCompressed)
        {
            bool flag = DeltaCompressionWrite(view, data);
            view.lastOnSerializeDataSent = lastData;
            if (!flag)
            {
                return null;
            }
        }
        return data;
    }

    public void OnStatusChanged(StatusCode statusCode)
    {
        DisconnectCause cause;
        if (PhotonNetwork.logLevel >= PhotonLogLevel.Informational)
        {
            Debug.Log(string.Format("OnStatusChanged: {0}", statusCode.ToString()));
        }
        switch (statusCode)
        {
            case StatusCode.SecurityExceptionOnConnect:
            case StatusCode.ExceptionOnConnect:
            {
                states = PeerStates.PeerCreated;
                if (CustomAuthenticationValues != null)
                {
                    CustomAuthenticationValues.Secret = null;
                }
                cause = (DisconnectCause) statusCode;
                object[] parameters = new object[] { cause };
                SendMonoMessage(PhotonNetworkingMessage.OnFailedToConnectToPhoton, parameters);
                goto Label_04F1;
            }
            case StatusCode.Connect:
                if (states == PeerStates.ConnectingToNameServer)
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
                    {
                        Debug.Log("Connected to NameServer.");
                    }
                    server = ServerConnection.NameServer;
                    if (CustomAuthenticationValues != null)
                    {
                        CustomAuthenticationValues.Secret = null;
                    }
                }
                if (states == PeerStates.ConnectingToGameserver)
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
                    {
                        Debug.Log("Connected to gameserver.");
                    }
                    server = ServerConnection.GameServer;
                    states = PeerStates.ConnectedToGameserver;
                }
                if (states == PeerStates.ConnectingToMasterserver)
                {
                    if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
                    {
                        Debug.Log("Connected to masterserver.");
                    }
                    server = ServerConnection.MasterServer;
                    states = PeerStates.ConnectedToMaster;
                    if (IsInitialConnect)
                    {
                        IsInitialConnect = false;
                        SendMonoMessage(PhotonNetworkingMessage.OnConnectedToPhoton);
                    }
                }
                EstablishEncryption();
                if (IsAuthorizeSecretAvailable)
                {
                    didAuthenticate = OpAuthenticate(mAppId, mAppVersionPun, PlayerName, CustomAuthenticationValues, CloudRegion.ToString());
                    if (didAuthenticate)
                    {
                        states = PeerStates.Authenticating;
                    }
                }
                goto Label_04F1;

            case StatusCode.Disconnect:
                didAuthenticate = false;
                isFetchingFriends = false;
                if (server == ServerConnection.GameServer)
                {
                    LeftRoomCleanup();
                }
                if (server == ServerConnection.MasterServer)
                {
                    LeftLobbyCleanup();
                }
                if (states == PeerStates.DisconnectingFromMasterserver)
                {
                    if (Connect(mGameserver, ServerConnection.GameServer))
                    {
                        states = PeerStates.ConnectingToGameserver;
                    }
                }
                else if (states == PeerStates.DisconnectingFromGameserver || states == PeerStates.DisconnectingFromNameServer)
                {
                    if (Connect(MasterServerAddress, ServerConnection.MasterServer))
                    {
                        states = PeerStates.ConnectingToMasterserver;
                    }
                }
                else
                {
                    if (CustomAuthenticationValues != null)
                    {
                        CustomAuthenticationValues.Secret = null;
                    }
                    states = PeerStates.PeerCreated;
                    SendMonoMessage(PhotonNetworkingMessage.OnDisconnectedFromPhoton);
                }
                goto Label_04F1;

            case StatusCode.Exception:
            {
                if (!IsInitialConnect)
                {
                    states = PeerStates.PeerCreated;
                    cause = (DisconnectCause) statusCode;
                    object[] objArray3 = new object[] { cause };
                    SendMonoMessage(PhotonNetworkingMessage.OnConnectionFail, objArray3);
                    break;
                }
                Debug.LogError("Exception while connecting to: " + ServerAddress + ". Check if the server is available.");
                if (ServerAddress == null || ServerAddress.StartsWith("127.0.0.1"))
                {
                    Debug.LogWarning("The server address is 127.0.0.1 (localhost): Make sure the server is running on this machine. Android and iOS emulators have their own localhost.");
                    if (ServerAddress == mGameserver)
                    {
                        Debug.LogWarning("This might be a misconfiguration in the game server config. You need to edit it to a (public) address.");
                    }
                }
                states = PeerStates.PeerCreated;
                cause = (DisconnectCause) statusCode;
                object[] objArray2 = new object[] { cause };
                SendMonoMessage(PhotonNetworkingMessage.OnFailedToConnectToPhoton, objArray2);
                break;
            }
            case StatusCode.QueueOutgoingReliableWarning:
            case StatusCode.QueueOutgoingUnreliableWarning:
            case StatusCode.SendError:
            case StatusCode.QueueOutgoingAcksWarning:
            case StatusCode.QueueSentWarning:
                goto Label_04F1;

            case StatusCode.QueueIncomingReliableWarning:
            case StatusCode.QueueIncomingUnreliableWarning:
                Debug.Log(statusCode + ". This client buffers many incoming messages. This is OK temporarily. With lots of these warnings, check if you send too much or execute messages too slow. " + (!PhotonNetwork.isMessageQueueRunning ? "Your isMessageQueueRunning is false. This can cause the issue temporarily." : string.Empty));
                goto Label_04F1;

            case StatusCode.ExceptionOnReceive:
            case StatusCode.TimeoutDisconnect:
            case StatusCode.DisconnectByServer:
            case StatusCode.DisconnectByServerUserLimit:
            case StatusCode.DisconnectByServerLogic:
                if (!IsInitialConnect)
                {
                    cause = (DisconnectCause) statusCode;
                    object[] objArray6 = new object[] { cause };
                    SendMonoMessage(PhotonNetworkingMessage.OnConnectionFail, objArray6);
                }
                else
                {
                    Debug.LogWarning(string.Concat(statusCode, " while connecting to: ", ServerAddress, ". Check if the server is available."));
                    cause = (DisconnectCause) statusCode;
                    object[] objArray5 = new object[] { cause };
                    SendMonoMessage(PhotonNetworkingMessage.OnFailedToConnectToPhoton, objArray5);
                }
                if (CustomAuthenticationValues != null)
                {
                    CustomAuthenticationValues.Secret = null;
                }
                Disconnect();
                goto Label_04F1;

            case StatusCode.EncryptionEstablished:
                if (server == ServerConnection.NameServer)
                {
                    states = PeerStates.ConnectedToNameServer;
                    if (!didAuthenticate && CloudRegion == CloudRegionCode.none)
                    {
                        OpGetRegions(mAppId);
                    }
                }
                if (!didAuthenticate && (!IsUsingNameServer || CloudRegion != CloudRegionCode.none))
                {
                    didAuthenticate = OpAuthenticate(mAppId, mAppVersionPun, PlayerName, CustomAuthenticationValues, CloudRegion.ToString());
                    if (didAuthenticate)
                    {
                        states = PeerStates.Authenticating;
                    }
                }
                goto Label_04F1;

            case StatusCode.EncryptionFailedToEstablish:
                Debug.LogError("Encryption wasn't established: " + statusCode + ". Going to authenticate anyways.");
                OpAuthenticate(mAppId, mAppVersionPun, PlayerName, CustomAuthenticationValues, CloudRegion.ToString());
                goto Label_04F1;

            default:
                Debug.LogError("Received unknown status code: " + statusCode);
                goto Label_04F1;
        }
        Disconnect();
    Label_04F1:
        externalListener.OnStatusChanged(statusCode);
    }

    public void OpCleanRpcBuffer(PhotonView view)
    {
        Hashtable customEventContent = new Hashtable();
        customEventContent[(byte) 0] = view.viewID;
        RaiseEventOptions raiseEventOptions = new RaiseEventOptions {
            CachingOption = EventCaching.RemoveFromRoomCache
        };
        OpRaiseEvent(200, customEventContent, true, raiseEventOptions);
    }

    public void OpCleanRpcBuffer(int actorNumber)
    {
        RaiseEventOptions options = new RaiseEventOptions {
            CachingOption = EventCaching.RemoveFromRoomCache
        };
        options.TargetActors = new int[] { actorNumber };
        RaiseEventOptions raiseEventOptions = options;
        OpRaiseEvent(200, null, true, raiseEventOptions);
    }

    public bool OpCreateGame(string roomName, RoomOptions roomOptions, TypedLobby typedLobby)
    {
        bool flag;
        if (!(flag = server == ServerConnection.GameServer))
        {
            mRoomOptionsForCreate = roomOptions;
            mRoomToGetInto = new Room(roomName, roomOptions);
            mRoomToEnterLobby = typedLobby ?? (!insideLobby ? null : lobby);
        }
        mLastJoinType = JoinType.CreateGame;
        return OpCreateRoom(roomName, roomOptions, mRoomToEnterLobby, GetLocalActorProperties(), flag);
    }

    public override bool OpFindFriends(string[] friendsToFind)
    {
        if (isFetchingFriends)
        {
            return false;
        }
        friendListRequested = friendsToFind;
        isFetchingFriends = true;
        return base.OpFindFriends(friendsToFind);
    }

    public override bool OpJoinRandomRoom(Hashtable expectedCustomRoomProperties, byte expectedMaxPlayers, Hashtable playerProperties, MatchmakingMode matchingType, TypedLobby typedLobby, string sqlLobbyFilter)
    {
        mRoomToGetInto = new Room(null, null);
        mRoomToEnterLobby = null;
        mLastJoinType = JoinType.JoinRandomGame;
        return base.OpJoinRandomRoom(expectedCustomRoomProperties, expectedMaxPlayers, playerProperties, matchingType, typedLobby, sqlLobbyFilter);
    }

    public bool OpJoinRoom(string roomName, RoomOptions roomOptions, TypedLobby typedLobby, bool createIfNotExists)
    {
        bool flag;
        if (!(flag = server == ServerConnection.GameServer))
        {
            mRoomOptionsForCreate = roomOptions;
            mRoomToGetInto = new Room(roomName, roomOptions);
            mRoomToEnterLobby = null;
            if (createIfNotExists)
            {
                mRoomToEnterLobby = typedLobby ?? (!insideLobby ? null : lobby);
            }
        }
        mLastJoinType = !createIfNotExists ? JoinType.JoinGame : JoinType.JoinOrCreateOnDemand;
        return base.OpJoinRoom(roomName, roomOptions, mRoomToEnterLobby, createIfNotExists, GetLocalActorProperties(), flag);
    }

    public virtual bool OpLeave()
    {
        if (states != PeerStates.Joined)
        {
            Debug.LogWarning("Not sending leave operation. State is not 'Joined': " + states);
            return false;
        }
        return OpCustom(254, null, true, 0);
    }

    public override bool OpRaiseEvent(byte eventCode, object customEventContent, bool sendReliable, RaiseEventOptions raiseEventOptions)
    {
        return !PhotonNetwork.offlineMode && base.OpRaiseEvent(eventCode, customEventContent, sendReliable, raiseEventOptions);
    }

    public void OpRemoveCompleteCache()
    {
        var raiseEventOptions = new RaiseEventOptions {
            CachingOption = EventCaching.RemoveFromRoomCache,
            Receivers = ReceiverGroup.MasterClient
        };
        OpRaiseEvent(0, null, true, raiseEventOptions);
    }

    public void OpRemoveCompleteCacheOfPlayer(int actorNumber)
    {
        var raiseEventOptions = new RaiseEventOptions
        {
            CachingOption = EventCaching.RemoveFromRoomCache,
            TargetActors = new int[] {actorNumber}
        };
        OpRaiseEvent(0, null, true, raiseEventOptions);
    }

    private void OpRemoveFromServerInstantiationsOfPlayer(int actorNr)
    {
        var raiseEventOptions = new RaiseEventOptions
        {
            CachingOption = EventCaching.RemoveFromRoomCache,
            TargetActors = new int[] {actorNr}
        };
        OpRaiseEvent(202, null, true, raiseEventOptions);
    }

    private void ReadoutProperties(Hashtable gameProperties, Hashtable pActorProperties, int targetActorNr)
    {
        if (mCurrentGame != null && gameProperties != null)
        {
            mCurrentGame.CacheProperties(gameProperties);
            var parameters = new object[] { gameProperties };
            SendMonoMessage(PhotonNetworkingMessage.OnPhotonCustomRoomPropertiesChanged, parameters);
            if (PhotonNetwork.automaticallySyncScene)
            {
                LoadLevelIfSynced();
            }
        }
        if (pActorProperties != null && pActorProperties.Count > 0)
        {
            if (targetActorNr > 0)
            {
                PhotonPlayer playerWithID = GetPlayerWithID(targetActorNr);
                if (playerWithID != null)
                {
                    Hashtable actorPropertiesForActorNr = GetActorPropertiesForActorNr(pActorProperties, targetActorNr);
                    playerWithID.InternalCacheProperties(actorPropertiesForActorNr);
                    object[] objArray2 = new object[] { playerWithID, actorPropertiesForActorNr };
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonPlayerPropertiesChanged, objArray2);
                }
            }
            else
            {
                foreach (object obj2 in pActorProperties.Keys)
                {
                    int number = (int) obj2;
                    Hashtable properties = (Hashtable) pActorProperties[obj2];
                    string name = (string) properties[(byte) 255];
                    PhotonPlayer player = GetPlayerWithID(number);
                    if (player == null)
                    {
                        player = new PhotonPlayer(false, number, name);
                        AddNewPlayer(number, player);
                    }
                    player.InternalCacheProperties(properties);
                    object[] objArray3 = new object[] { player, properties };
                    SendMonoMessage(PhotonNetworkingMessage.OnPhotonPlayerPropertiesChanged, objArray3);
                }
            }
        }
    }

    private void RebuildPlayerListCopies()
    {
        mPlayerListCopy = new PhotonPlayer[mActors.Count];
        mActors.Values.CopyTo(mPlayerListCopy, 0);
        List<PhotonPlayer> list = new List<PhotonPlayer>();
        foreach (PhotonPlayer player in mPlayerListCopy)
        {
            if (!player.isLocal)
            {
                list.Add(player);
            }
        }
        mOtherPlayerListCopy = list.ToArray();
    }

    public void RegisterPhotonView(PhotonView netView)
    {
        if (!Application.isPlaying)
        {
            photonViewList = new Dictionary<int, PhotonView>();
        }
        else if (netView.subId != 0)
        {
            if (photonViewList.ContainsKey(netView.viewID))
            {
                if (netView != photonViewList[netView.viewID])
                {
                    Debug.LogError(string.Format("PhotonView ID duplicate found: {0}. New: {1} old: {2}. Maybe one wasn't destroyed on scene load?! Check for 'DontDestroyOnLoad'. Destroying old entry, adding new.", netView.viewID, netView, photonViewList[netView.viewID]));
                }
                RemoveInstantiatedGO(photonViewList[netView.viewID].gameObject, true);
            }
            photonViewList.Add(netView.viewID, netView);
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
            {
                Debug.Log("Registered PhotonView: " + netView.viewID);
            }
        }
    }

    public void RemoveAllInstantiatedObjects()
    {
        GameObject[] array = new GameObject[instantiatedObjects.Count];
        instantiatedObjects.Values.CopyTo(array, 0);
        for (int i = 0; i < array.Length; i++)
        {
            GameObject go = array[i];
            if (go != null)
            {
                RemoveInstantiatedGO(go, false);
            }
        }
        if (instantiatedObjects.Count > 0)
        {
            Debug.LogError("RemoveAllInstantiatedObjects() this.instantiatedObjects.Count should be 0 by now.");
        }
        instantiatedObjects = new Dictionary<int, GameObject>();
    }

    private void RemoveCacheOfLeftPlayers()
    {
        Dictionary<byte, object> customOpParameters = new Dictionary<byte, object>();
        customOpParameters[244] = (byte) 0;
        customOpParameters[247] = (byte) 7;
        OpCustom(253, customOpParameters, true, 0);
    }

    public void RemoveInstantiatedGO(GameObject go, bool localOnly)
    {
        if (go == null)
        {
            Debug.LogError("Failed to 'network-remove' GameObject because it's null.");
        }
        else
        {
            PhotonView[] componentsInChildren = go.GetComponentsInChildren<PhotonView>();
            if (componentsInChildren != null && componentsInChildren.Length > 0)
            {
                PhotonView view = componentsInChildren[0];
                int ownerActorNr = view.OwnerActorNr;
                int instantiationId = view.instantiationId;
                if (!localOnly)
                {
                    if (!view.isMine && (!mLocalActor.isMasterClient || mActors.ContainsKey(ownerActorNr)))
                    {
                        Debug.LogError("Failed to 'network-remove' GameObject. Client is neither owner nor masterClient taking over for owner who left: " + view);
                        return;
                    }
                    if (instantiationId < 1)
                    {
                        Debug.LogError("Failed to 'network-remove' GameObject because it is missing a valid InstantiationId on view: " + view + ". Not Destroying GameObject or PhotonViews!");
                        return;
                    }
                }
                if (!localOnly)
                {
                    ServerCleanInstantiateAndDestroy(instantiationId, ownerActorNr);
                }
                instantiatedObjects.Remove(instantiationId);
                for (int i = componentsInChildren.Length - 1; i >= 0; i--)
                {
                    PhotonView view2 = componentsInChildren[i];
                    if (view2 != null)
                    {
                        if (view2.instantiationId >= 1)
                        {
                            LocalCleanPhotonView(view2);
                        }
                        if (!localOnly)
                        {
                            OpCleanRpcBuffer(view2);
                        }
                    }
                }
                if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
                {
                    Debug.Log("Network destroy Instantiated GO: " + go.name);
                }
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                Debug.LogError("Failed to 'network-remove' GameObject because has no PhotonView components: " + go);
            }
        }
    }

    private void RemoveInstantiationData(int instantiationId)
    {
        tempInstantiationData.Remove(instantiationId);
    }

    private void RemovePlayer(int ID, PhotonPlayer player)
    {
        mActors.Remove(ID);
        if (!player.isLocal)
        {
            RebuildPlayerListCopies();
        }
    }

    public void RemoveRPCsInGroup(int group)
    {
        foreach (KeyValuePair<int, PhotonView> pair in photonViewList)
        {
            PhotonView view = pair.Value;
            if (view.group == group)
            {
                CleanRpcBufferIfMine(view);
            }
        }
    }

    private void ResetPhotonViewsOnSerialize()
    {
        foreach (PhotonView view in photonViewList.Values)
        {
            view.lastOnSerializeDataSent = null;
        }
    }

    private static int ReturnLowestPlayerId(PhotonPlayer[] players, int playerIdToIgnore)
    {
        if (players == null || players.Length == 0)
        {
            return -1;
        }
        int iD = 2147483647;
        for (int i = 0; i < players.Length; i++)
        {
            PhotonPlayer player = players[i];
            if (player.ID != playerIdToIgnore && player.ID < iD)
            {
                iD = player.ID;
            }
        }
        return iD;
    }

    internal void RPC(PhotonView view, string methodName, PhotonPlayer player, params object[] parameters)
    {
        if (!blockSendingGroups.Contains(view.group))
        {
            if (view.viewID < 1)
            {
                Debug.LogError(string.Concat("Illegal view ID:", view.viewID, " method: ", methodName, " GO:", view.gameObject.name));
            }
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
            {
                Debug.Log(string.Concat("Sending RPC \"", methodName, "\" to player[", player, "]"));
            }
            Hashtable rpcData = new Hashtable {[(byte) 0] = view.viewID};
            if (view.prefix > 0)
            {
                rpcData[(byte) 1] = (short) view.prefix;
            }
            rpcData[(byte) 2] = ServerTimeInMilliSeconds;
            int num;
            if (rpcShortcuts.TryGetValue(methodName, out num))
            {
                rpcData[(byte) 5] = (byte) num;
            }
            else
            {
                rpcData[(byte) 3] = methodName;
            }
            if (parameters != null && parameters.Length > 0)
            {
                rpcData[(byte) 4] = parameters;
            }
            if (Equals(mLocalActor, player))
            {
                ExecuteRPC(rpcData, player);
            }
            else
            {
                RaiseEventOptions options = new RaiseEventOptions();
                options.TargetActors = new int[] { player.ID };
                RaiseEventOptions raiseEventOptions = options;
                OpRaiseEvent(200, rpcData, true, raiseEventOptions);
            }
        }
    }

    internal void RPC(PhotonView view, string methodName, PhotonTargets target, params object[] parameters)
    {
        if (!blockSendingGroups.Contains(view.group))
        {
            RaiseEventOptions options;
            if (view.viewID < 1)
            {
                Debug.LogError(string.Concat("Illegal view ID:", view.viewID, " method: ", methodName, " GO:", view.gameObject.name));
            }
            if (PhotonNetwork.logLevel >= PhotonLogLevel.Full)
            {
                Debug.Log(string.Concat("Sending RPC \"", methodName, "\" to ", target));
            }
            Hashtable customEventContent = new Hashtable();
            customEventContent[(byte) 0] = view.viewID;
            if (view.prefix > 0)
            {
                customEventContent[(byte) 1] = (short) view.prefix;
            }
            customEventContent[(byte) 2] = ServerTimeInMilliSeconds;
            int num = 0;
            if (rpcShortcuts.TryGetValue(methodName, out num))
            {
                customEventContent[(byte) 5] = (byte) num;
            }
            else
            {
                customEventContent[(byte) 3] = methodName;
            }
            if (parameters != null && parameters.Length > 0)
            {
                customEventContent[(byte) 4] = parameters;
            }
            if (target == PhotonTargets.All)
            {
                options = new RaiseEventOptions {
                    InterestGroup = (byte) view.group
                };
                RaiseEventOptions raiseEventOptions = options;
                OpRaiseEvent(200, customEventContent, true, raiseEventOptions);
                ExecuteRPC(customEventContent, mLocalActor);
            }
            else if (target == PhotonTargets.Hawk)
            {
                PhotonPlayer hawk = PhotonNetwork.playerList.FirstOrDefault(p => p.customProperties[PhotonPlayerProperty.AoTTG_Mod] != null);
                if (hawk == null) return;
                options = new RaiseEventOptions {
                    TargetActors = new []{hawk.ID}
                };
                OpRaiseEvent(200, customEventContent, true, options);
            }
            else if (target == PhotonTargets.Users)
            {
                options = new RaiseEventOptions {
                    TargetActors = PhotonNetwork.playerList.Where(x => x.customProperties[PhotonPlayerProperty.HawkUser] != null || x.customProperties[PhotonPlayerProperty.AoTTG_Mod] != null).Select(x => x.ID).ToArray()
                };
                OpRaiseEvent(200, customEventContent, true, options/*new RaiseEventOptions {TargetActors = ids, InterestGroup = (byte) view.group}*/);
            }
            else if (target == PhotonTargets.Others)
            {
                options = new RaiseEventOptions {
                    InterestGroup = (byte) view.group
                };
                RaiseEventOptions options3 = options;
                OpRaiseEvent(200, customEventContent, true, options3);
            }
            else if (target == PhotonTargets.AllBuffered)
            {
                options = new RaiseEventOptions {
                    CachingOption = EventCaching.AddToRoomCache
                };
                RaiseEventOptions options4 = options;
                OpRaiseEvent(200, customEventContent, true, options4);
                ExecuteRPC(customEventContent, mLocalActor);
            }
            else if (target == PhotonTargets.OthersBuffered)
            {
                options = new RaiseEventOptions {
                    CachingOption = EventCaching.AddToRoomCache
                };
                RaiseEventOptions options5 = options;
                OpRaiseEvent(200, customEventContent, true, options5);
            }
            else if (target == PhotonTargets.MasterClient)
            {
                if (mMasterClient == mLocalActor)
                {
                    ExecuteRPC(customEventContent, mLocalActor);
                }
                else
                {
                    options = new RaiseEventOptions {
                        Receivers = ReceiverGroup.MasterClient
                    };
                    RaiseEventOptions options6 = options;
                    OpRaiseEvent(200, customEventContent, true, options6);
                }
            }
            else if (target == PhotonTargets.AllViaServer)
            {
                options = new RaiseEventOptions {
                    InterestGroup = (byte) view.group,
                    Receivers = ReceiverGroup.All
                };
                RaiseEventOptions options7 = options;
                OpRaiseEvent(200, customEventContent, true, options7);
            }
            else if (target == PhotonTargets.AllBufferedViaServer)
            {
                options = new RaiseEventOptions {
                    InterestGroup = (byte) view.group,
                    Receivers = ReceiverGroup.All,
                    CachingOption = EventCaching.AddToRoomCache
                };
                RaiseEventOptions options8 = options;
                OpRaiseEvent(200, customEventContent, true, options8);
            }
            else
            {
                Debug.LogError("Unsupported target enum: " + target);
            }
        }
    }

    public void RunViewUpdate()
    {
        if (PhotonNetwork.connected && !PhotonNetwork.offlineMode && mActors != null && mActors.Count > 1)
        {
            Dictionary<int, Hashtable> dictionary = new Dictionary<int, Hashtable>();
            Dictionary<int, Hashtable> dictionary2 = new Dictionary<int, Hashtable>();
            foreach (KeyValuePair<int, PhotonView> pair in photonViewList)
            {
                PhotonView view = pair.Value;
                if (view.observed != null && view.synchronization != ViewSynchronization.Off && (view.ownerId == mLocalActor.ID || view.isSceneView && mMasterClient == mLocalActor) && view.gameObject.activeInHierarchy && !blockSendingGroups.Contains(view.group))
                {
                    Hashtable hashtable = OnSerializeWrite(view);
                    if (hashtable != null)
                    {
                        if (view.synchronization != ViewSynchronization.ReliableDeltaCompressed && !view.mixedModeIsReliable)
                        {
                            if (!dictionary2.ContainsKey(view.group))
                            {
                                dictionary2[view.group] = new Hashtable();
                                dictionary2[view.group][(byte) 0] = ServerTimeInMilliSeconds;
                                if (currentLevelPrefix >= 0)
                                {
                                    dictionary2[view.group][(byte) 1] = currentLevelPrefix;
                                }
                            }
                            Hashtable hashtable3 = dictionary2[view.group];
                            hashtable3.Add((short) hashtable3.Count, hashtable);
                        }
                        else if (hashtable.ContainsKey((byte) 1) || hashtable.ContainsKey((byte) 2))
                        {
                            if (!dictionary.ContainsKey(view.group))
                            {
                                dictionary[view.group] = new Hashtable();
                                dictionary[view.group][(byte) 0] = ServerTimeInMilliSeconds;
                                if (currentLevelPrefix >= 0)
                                {
                                    dictionary[view.group][(byte) 1] = currentLevelPrefix;
                                }
                            }
                            Hashtable hashtable2 = dictionary[view.group];
                            hashtable2.Add((short) hashtable2.Count, hashtable);
                        }
                    }
                }
            }
            RaiseEventOptions raiseEventOptions = new RaiseEventOptions();
            foreach (KeyValuePair<int, Hashtable> pair2 in dictionary)
            {
                raiseEventOptions.InterestGroup = (byte) pair2.Key;
                OpRaiseEvent(206, pair2.Value, true, raiseEventOptions);
            }
            foreach (KeyValuePair<int, Hashtable> pair3 in dictionary2)
            {
                raiseEventOptions.InterestGroup = (byte) pair3.Key;
                OpRaiseEvent(201, pair3.Value, false, raiseEventOptions);
            }
        }
    }

    private void SendDestroyOfAll()
    {
        Hashtable customEventContent = new Hashtable();
        customEventContent[(byte) 0] = -1;
        OpRaiseEvent(207, customEventContent, true, null);
    }

    private void SendDestroyOfPlayer(int actorNr)
    {
        Hashtable customEventContent = new Hashtable();
        customEventContent[(byte) 0] = actorNr;
        OpRaiseEvent(207, customEventContent, true, null);
    }

    internal Hashtable SendInstantiate(string prefabName, Vector3 position, Quaternion rotation, int group, int[] viewIDs, object[] data, bool isGlobalObject)
    {
        int num = viewIDs[0];
        Hashtable customEventContent = new Hashtable();
        customEventContent[(byte) 0] = prefabName;
        if (position != Vector3.zero)
        {
            customEventContent[(byte) 1] = position;
        }
        if (rotation != Quaternion.identity)
        {
            customEventContent[(byte) 2] = rotation;
        }
        if (group != 0)
        {
            customEventContent[(byte) 3] = group;
        }
        if (viewIDs.Length > 1)
        {
            customEventContent[(byte) 4] = viewIDs;
        }
        if (data != null)
        {
            customEventContent[(byte) 5] = data;
        }
        if (currentLevelPrefix > 0)
        {
            customEventContent[(byte) 8] = currentLevelPrefix;
        }
        customEventContent[(byte) 6] = ServerTimeInMilliSeconds;
        customEventContent[(byte) 7] = num;
        RaiseEventOptions raiseEventOptions = new RaiseEventOptions {
            CachingOption = !isGlobalObject ? EventCaching.AddToRoomCache : EventCaching.AddToRoomCacheGlobal
        };
        OpRaiseEvent(202, customEventContent, true, raiseEventOptions);
        return customEventContent;
    }

    public static void SendMonoMessage(PhotonNetworkingMessage methodString, params object[] parameters)
    {
        HashSet<GameObject> sendMonoMessageTargets;
        if (PhotonNetwork.SendMonoMessageTargets != null)
        {
            sendMonoMessageTargets = PhotonNetwork.SendMonoMessageTargets;
        }
        else
        {
            sendMonoMessageTargets = new HashSet<GameObject>();
            Component[] componentArray = (Component[]) UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour));
            for (int i = 0; i < componentArray.Length; i++)
            {
                sendMonoMessageTargets.Add(componentArray[i].gameObject);
            }
        }
        string methodName = methodString.ToString();
        foreach (GameObject obj2 in sendMonoMessageTargets)
        {
            if (parameters != null && parameters.Length == 1)
            {
                obj2.SendMessage(methodName, parameters[0], SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                obj2.SendMessage(methodName, parameters, SendMessageOptions.DontRequireReceiver);
            }
        }
    }

    private void SendPlayerName()
    {
        if (states == PeerStates.Joining)
        {
            mPlayernameHasToBeUpdated = true;
        }
        else if (mLocalActor != null)
        {
            mLocalActor.name = PlayerName;
            Hashtable actorProperties = new Hashtable();
            actorProperties[(byte) 255] = PlayerName;
            if (mLocalActor.ID > 0)
            {
                OpSetPropertiesOfActor(mLocalActor.ID, actorProperties, true, 0);
                mPlayernameHasToBeUpdated = false;
            }
        }
    }

    private void ServerCleanInstantiateAndDestroy(int instantiateId, int actorNr)
    {
        Hashtable customEventContent = new Hashtable();
        customEventContent[(byte) 7] = instantiateId;
        RaiseEventOptions options = new RaiseEventOptions {
            CachingOption = EventCaching.RemoveFromRoomCache
        };
        options.TargetActors = new int[] { actorNr };
        RaiseEventOptions raiseEventOptions = options;
        OpRaiseEvent(202, customEventContent, true, raiseEventOptions);
        Hashtable hashtable2 = new Hashtable();
        hashtable2[(byte) 0] = instantiateId;
        OpRaiseEvent(204, hashtable2, true, null);
    }

    public void SetApp(string appId, string gameVersion)
    {
        mAppId = appId.Trim();
        if (!string.IsNullOrEmpty(gameVersion))
        {
            mAppVersion = gameVersion.Trim();
        }
    }

    protected internal void SetLevelInPropsIfSynced(object levelId)
    {
        if (PhotonNetwork.automaticallySyncScene && PhotonNetwork.isMasterClient && PhotonNetwork.room != null)
        {
            if (levelId == null)
            {
                Debug.LogError("Parameter levelId can't be null!");
            }
            else
            {
                if (PhotonNetwork.room.customProperties.ContainsKey("curScn"))
                {
                    object obj2 = PhotonNetwork.room.customProperties["curScn"];
                    if (obj2 is int && Application.loadedLevel == (int) obj2)
                    {
                        return;
                    }
                    if (obj2 is string && Application.loadedLevelName.Equals((string) obj2))
                    {
                        return;
                    }
                }
                Hashtable propertiesToSet = new Hashtable();
                if (levelId is int)
                {
                    propertiesToSet["curScn"] = (int) levelId;
                }
                else if (levelId is string)
                {
                    propertiesToSet["curScn"] = (string) levelId;
                }
                else
                {
                    Debug.LogError("Parameter levelId must be int or string!");
                }
                PhotonNetwork.room.SetCustomProperties(propertiesToSet);
                SendOutgoingCommands();
            }
        }
    }

    public void SetLevelPrefix(short prefix)
    {
        currentLevelPrefix = prefix;
    }

    protected internal bool SetMasterClient(int playerId, bool sync)
    {
        if (mMasterClient == null || mMasterClient.ID == -1 || !mActors.ContainsKey(playerId))
        {
            return false;
        }
        if (sync)
        {
            Hashtable customEventContent = new Hashtable { {(byte) 1, playerId} };
            if (!OpRaiseEvent(208, customEventContent, true, null))
            {
                return false;
            }
        }
        hasSwitchedMC = true;
        mMasterClient = mActors[playerId];
        object[] parameters = new object[] { mMasterClient };
        SendMonoMessage(PhotonNetworkingMessage.OnMasterClientSwitched, parameters);
        return true;
    }

    public void SetReceivingEnabled(int group, bool enabled)
    {
        if (group <= 0)
        {
            Debug.LogError("Error: PhotonNetwork.SetReceivingEnabled was called with an illegal group number: " + group + ". The group number should be at least 1.");
        }
        else if (enabled)
        {
            if (!allowedReceivingGroups.Contains(group))
            {
                allowedReceivingGroups.Add(group);
                byte[] groupsToAdd = new byte[] { (byte) group };
                OpChangeGroups(null, groupsToAdd);
            }
        }
        else if (allowedReceivingGroups.Contains(group))
        {
            allowedReceivingGroups.Remove(group);
            byte[] groupsToRemove = new byte[] { (byte) group };
            OpChangeGroups(groupsToRemove, null);
        }
    }

    public void SetReceivingEnabled(int[] enableGroups, int[] disableGroups)
    {
        List<byte> list = new List<byte>();
        List<byte> list2 = new List<byte>();
        if (enableGroups != null)
        {
            for (int i = 0; i < enableGroups.Length; i++)
            {
                int item = enableGroups[i];
                if (item <= 0)
                {
                    Debug.LogError("Error: PhotonNetwork.SetReceivingEnabled was called with an illegal group number: " + item + ". The group number should be at least 1.");
                }
                else if (!allowedReceivingGroups.Contains(item))
                {
                    allowedReceivingGroups.Add(item);
                    list.Add((byte) item);
                }
            }
        }
        if (disableGroups != null)
        {
            for (int j = 0; j < disableGroups.Length; j++)
            {
                int num4 = disableGroups[j];
                if (num4 <= 0)
                {
                    Debug.LogError("Error: PhotonNetwork.SetReceivingEnabled was called with an illegal group number: " + num4 + ". The group number should be at least 1.");
                }
                else if (list.Contains((byte) num4))
                {
                    Debug.LogError("Error: PhotonNetwork.SetReceivingEnabled disableGroups contains a group that is also in the enableGroups: " + num4 + ".");
                }
                else if (allowedReceivingGroups.Contains(num4))
                {
                    allowedReceivingGroups.Remove(num4);
                    list2.Add((byte) num4);
                }
            }
        }
        OpChangeGroups(list2.Count <= 0 ? null : list2.ToArray(), list.Count <= 0 ? null : list.ToArray());
    }

    public void SetSendingEnabled(int group, bool enabled)
    {
        if (!enabled)
        {
            blockSendingGroups.Add(group);
        }
        else
        {
            blockSendingGroups.Remove(group);
        }
    }

    public void SetSendingEnabled(int[] enableGroups, int[] disableGroups)
    {
        if (enableGroups != null)
        {
            foreach (int num2 in enableGroups)
            {
                if (blockSendingGroups.Contains(num2))
                {
                    blockSendingGroups.Remove(num2);
                }
            }
        }
        if (disableGroups != null)
        {
            foreach (int num4 in disableGroups)
            {
                if (!blockSendingGroups.Contains(num4))
                {
                    blockSendingGroups.Add(num4);
                }
            }
        }
    }

    private void StoreInstantiationData(int instantiationId, object[] instantiationData)
    {
        tempInstantiationData[instantiationId] = instantiationData;
    }

    public bool WebRpc(string uriPath, object parameters)
    {
        Dictionary<byte, object> customOpParameters = new Dictionary<byte, object>();
        customOpParameters.Add(209, uriPath);
        customOpParameters.Add(208, parameters);
        return OpCustom(219, customOpParameters, true);
    }

    public List<Region> AvailableRegions { get; protected internal set; }

    public CloudRegionCode CloudRegion { get; protected internal set; }

    public AuthenticationValues CustomAuthenticationValues { get; set; }

    protected internal int FriendsListAge
    {
        get
        {
            return isFetchingFriends || friendListTimestamp == 0 ? 0 : Environment.TickCount - friendListTimestamp;
        }
    }

    public bool IsAuthorizeSecretAvailable
    {
        get
        {
            return false;
        }
    }

    public bool IsUsingNameServer { get; protected internal set; }

    public TypedLobby lobby { get; set; }

    protected internal string mAppVersionPun
    {
        get
        {
            return string.Format("{0}_{1}", mAppVersion, "1.28");
        }
    }

    public string MasterServerAddress { get; protected internal set; }

    public Room mCurrentGame
    {
        get
        {
            if (mRoomToGetInto != null && mRoomToGetInto.isLocalClientInside)
            {
                return mRoomToGetInto;
            }
            return null;
        }
    }

    public int mGameCount { get; internal set; }

    public string mGameserver { get; internal set; }

    public PhotonPlayer mLocalActor { get; internal set; }

    public int mPlayersInRoomsCount { get; internal set; }

    public int mPlayersOnMasterCount { get; internal set; }

    public int mQueuePosition { get; internal set; }

    internal RoomOptions mRoomOptionsForCreate { get; set; }

    internal TypedLobby mRoomToEnterLobby { get; set; }

    internal Room mRoomToGetInto { get; set; }

    public string PlayerName
    {
        get
        {
            return playername;
        }
        set
        {
            if (!string.IsNullOrEmpty(value) && !value.Equals(playername))
            {
                if (mLocalActor != null)
                {
                    mLocalActor.name = value;
                }
                playername = value;
                if (mCurrentGame != null)
                {
                    SendPlayerName();
                }
            }
        }
    }

    protected internal ServerConnection server { get; private set; }

    public PeerStates states { get; internal set; }
}

