﻿// vis2k:
// base class for NetworkTransform and NetworkTransformChild.
// New method is simple and stupid. No more 1500 lines of code.
//
// Server sends current data.
// Client saves it and interpolates last and latest data points.
//   Update handles transform movement / rotation
//   FixedUpdate handles rigidbody movement / rotation
//
// Notes:
// * Built-in Teleport detection in case of lags / teleport / obstacles
// * Quaternion > EulerAngles because gimbal lock and Quaternion.Slerp
// * Syncs XYZ. Works 3D and 2D. Saving 4 bytes isn't worth 1000 lines of code.
// * Initial delay might happen if server sends packet immediately after moving
//   just 1cm, hence we move 1cm and then wait 100ms for next packet
// * Only way for smooth movement is to use a fixed movement speed during
//   interpolation. interpolation over time is never that good.
//
using System;
using UnityEngine;

namespace Mirror.Experimental
{
    public abstract class NetworkTransformBase : NetworkBehaviour
    {
        [Header("Authority")]
        [Tooltip("Set to true if moves come from owner client, set to false if moves always come from server")]
        public bool clientAuthority;

        [Tooltip("Set to true if updates from server should be ignored by owner in server authority mode")]
        public bool excludeOwnerUpdate;

        // Is this a client with authority over this transform?
        // This component could be on the player object or any object that has been assigned authority to this client.
        bool IsClientWithAuthority => hasAuthority && clientAuthority;

        // Is this a client in server authority mode
        // This component could be on the player object or any object that has been assigned authority to this client.
        bool IsClientWithServerAuthority => hasAuthority && !clientAuthority;

        // Sensitivity is added for VR where human players tend to have micro movements so this can quiet down
        // the network traffic.  Additionally, rigidbody drift should send less traffic, e.g very slow sliding / rolling.
        [Header("Sensitivity")]
        [Tooltip("Changes to the transform must exceed these values to be transmitted on the network.")]
        public float localPositionSensitivity = .01f;
        [Tooltip("If rotation exceeds this angle, it will be transmitted on the network")]
        public float localRotationSensitivity = .01f;
        [Tooltip("Changes to the transform must exceed these values to be transmitted on the network.")]
        public float localScaleSensitivity = .01f;

        // target transform to sync. can be on a child.
        protected abstract Transform targetComponent { get; }

        // server
        Vector3 lastPosition;
        Quaternion lastRotation;
        Vector3 lastScale;

        //client
        [Serializable]
        public struct ServerData
        {
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
        }

        [SyncVar(hook = nameof(OnServerDataChanged))]
        public ServerData serverData = new ServerData();

        void OnServerDataChanged(ServerData _, ServerData newServerData)
        {
            if (IsClientWithServerAuthority && excludeOwnerUpdate) return;

            SetGoal(newServerData);
        }

        // use local position/rotation for VR support
        public struct DataPoint
        {
            public float timeStamp;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
            public float movementSpeed;
        }

        // interpolation start and goal
        DataPoint start = new DataPoint();
        DataPoint goal = new DataPoint();

        // local authority send time
        float lastClientSendTime;
        float lastServerSendTime;

        void LateUpdate()
        {
            // if server then always sync to others.
            if (isServer)
            {
                // let the clients know that this has moved
                if (Time.time - lastServerSendTime >= syncInterval && HasEitherMovedRotatedScaled())
                {
                    serverData = new ServerData
                    {
                        localPosition = targetComponent.transform.localPosition,
                        localRotation = targetComponent.transform.localRotation,
                        localScale = targetComponent.transform.localScale
                    };
                    //RpcMove(targetComponent.transform.localPosition, targetComponent.transform.localRotation, targetComponent.transform.localScale);
                    lastServerSendTime = Time.time;
                }
            }

            if (isClient)
            {
                // send to server if we have local authority (and aren't the server)
                // -> only if connectionToServer has been initialized yet too
                if (IsClientWithAuthority)
                {
                    // check only each 'syncInterval'
                    if (!isServer && Time.time - lastClientSendTime >= syncInterval)
                    {
                        if (HasEitherMovedRotatedScaled())
                        {
                            // serialize
                            // local position/rotation for VR support
                            // send to server
                            CmdClientToServerSync(new ServerData
                            {
                                localPosition = targetComponent.transform.localPosition,
                                localRotation = targetComponent.transform.localRotation,
                                localScale = targetComponent.transform.localScale
                            });

                            //CmdClientToServerSync(targetComponent.transform.localPosition, targetComponent.transform.localRotation, targetComponent.transform.localScale);
                        }
                        lastClientSendTime = Time.time;
                    }
                }
                else if (goal.timeStamp != 0)
                {
                    // teleport or interpolate
                    if (NeedsTeleport())
                    {
                        // local position/rotation for VR support
                        ApplyPositionRotationScale(goal.localPosition, goal.localRotation, goal.localScale);

                        // reset data points so we don't keep interpolating
                        start = new DataPoint();
                        goal = new DataPoint();
                    }
                    else
                    {
                        // local position/rotation for VR support
                        ApplyPositionRotationScale(InterpolatePosition(start, goal, targetComponent.transform.localPosition),
                                                   InterpolateRotation(start, goal, targetComponent.transform.localRotation),
                                                   InterpolateScale(start, goal, targetComponent.transform.localScale));
                    }

                }
            }
        }

        // moved since last time we checked it?
        bool HasEitherMovedRotatedScaled()
        {
            // moved or rotated or scaled?
            // local position/rotation/scale for VR support
            bool moved = Vector3.Distance(lastPosition, targetComponent.transform.localPosition) > localPositionSensitivity;
            bool scaled = Vector3.Distance(lastScale, targetComponent.transform.localScale) > localScaleSensitivity;
            bool rotated = Quaternion.Angle(lastRotation, targetComponent.transform.localRotation) > localRotationSensitivity;

            // save last for next frame to compare
            // (only if change was detected. otherwise slow moving objects might
            //  never sync because of C#'s float comparison tolerance. see also:
            //  https://github.com/vis2k/Mirror/pull/428)
            bool change = moved || rotated || scaled;
            if (change)
            {
                // local position/rotation for VR support
                lastPosition = targetComponent.transform.localPosition;
                lastRotation = targetComponent.transform.localRotation;
                lastScale = targetComponent.transform.localScale;
            }
            return change;
        }

        // teleport / lag / stuck detection
        // -> checking distance is not enough since there could be just a tiny
        //    fence between us and the goal
        // -> checking time always works, this way we just teleport if we still
        //    didn't reach the goal after too much time has elapsed
        bool NeedsTeleport()
        {
            // calculate time between the two data points
            float startTime = start.timeStamp != 0 ? start.timeStamp : Time.time - syncInterval;
            float goalTime = goal.timeStamp != 0 ? goal.timeStamp : Time.time;
            float difference = goalTime - startTime;
            float timeSinceGoalReceived = Time.time - goalTime;
            return timeSinceGoalReceived > difference * 5;
        }

        // local authority client sends sync message to server for broadcasting
        [Command]
        void CmdClientToServerSync(ServerData newServerData)
        {
            // Ignore messages from client if not in client authority mode
            if (!clientAuthority)
                return;

            // deserialize payload
            SetGoal(newServerData);

            // server-only mode does no interpolation to save computations,
            // but let's set the position directly
            if (isServer && !isClient)
                ApplyPositionRotationScale(goal.localPosition, goal.localRotation, goal.localScale);

            serverData = newServerData;
            //RpcMove(position, rotation, scale);
        }

        //[ClientRpc]
        //void RpcMove(Vector3 position, Quaternion rotation, Vector3 scale)
        //{
        //    if (!isServer)
        //        SetGoal(position, rotation, scale);
        //}

        // serialization is needed by OnSerialize and by manual sending from authority
        void SetGoal(ServerData newServerData)
        {
            // put it into a data point immediately
            DataPoint temp = new DataPoint
            {
                // deserialize position
                localPosition = newServerData.localPosition,
                localRotation = newServerData.localRotation,
                localScale = newServerData.localScale,
                timeStamp = Time.time,
            };

            // movement speed: based on how far it moved since last time
            // has to be calculated before 'start' is overwritten
            temp.movementSpeed = EstimateMovementSpeed(goal, temp, targetComponent.transform, syncInterval);

            // reassign start wisely
            // -> first ever data point? then make something up for previous one
            //    so that we can start interpolation without waiting for next.
            if (start.timeStamp == 0)
            {
                start = new DataPoint
                {
                    timeStamp = Time.time - syncInterval,
                    // local position/rotation for VR support
                    localPosition = targetComponent.transform.localPosition,
                    localRotation = targetComponent.transform.localRotation,
                    localScale = targetComponent.transform.localScale,
                    movementSpeed = temp.movementSpeed
                };
            }
            // -> second or nth data point? then update previous, but:
            //    we start at where ever we are right now, so that it's
            //    perfectly smooth and we don't jump anywhere
            //
            //    example if we are at 'x':
            //
            //        A--x->B
            //
            //    and then receive a new point C:
            //
            //        A--x--B
            //              |
            //              |
            //              C
            //
            //    then we don't want to just jump to B and start interpolation:
            //
            //              x
            //              |
            //              |
            //              C
            //
            //    we stay at 'x' and interpolate from there to C:
            //
            //           x..B
            //            \ .
            //             \.
            //              C
            //
            else
            {
                float oldDistance = Vector3.Distance(start.localPosition, goal.localPosition);
                float newDistance = Vector3.Distance(goal.localPosition, temp.localPosition);

                start = goal;

                // teleport / lag / obstacle detection: only continue at current
                // position if we aren't too far away
                //
                // local position/rotation for VR support
                if (Vector3.Distance(targetComponent.transform.localPosition, start.localPosition) < oldDistance + newDistance)
                {
                    start.localPosition = targetComponent.transform.localPosition;
                    start.localRotation = targetComponent.transform.localRotation;
                    start.localScale = targetComponent.transform.localScale;
                }
            }

            // set new destination in any case. new data is best data.
            goal = temp;
        }

        // try to estimate movement speed for a data point based on how far it
        // moved since the previous one
        // => if this is the first time ever then we use our best guess:
        //    -> delta based on transform.localPosition
        //    -> elapsed based on send interval hoping that it roughly matches
        static float EstimateMovementSpeed(DataPoint from, DataPoint to, Transform transform, float sendInterval)
        {
            Vector3 delta = to.localPosition - (from.localPosition != null ? from.localPosition : transform.localPosition);
            float elapsed = from.timeStamp != 0 ? to.timeStamp - from.timeStamp : sendInterval;
            // avoid NaN
            return elapsed > 0 ? delta.magnitude / elapsed : 0;
        }

        // set position carefully depending on the target component
        void ApplyPositionRotationScale(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            // local position/rotation for VR support
            targetComponent.transform.localPosition = position;
            targetComponent.transform.localRotation = rotation;
            targetComponent.transform.localScale = scale;
        }

        // where are we in the timeline between start and goal? [0,1]
        static Vector3 InterpolatePosition(DataPoint start, DataPoint goal, Vector3 currentPosition)
        {
            if (start.movementSpeed != 0)
            {
                // Option 1: simply interpolate based on time. but stutter
                // will happen, it's not that smooth. especially noticeable if
                // the camera automatically follows the player
                //   float t = CurrentInterpolationFactor();
                //   return Vector3.Lerp(start.position, goal.position, t);

                // Option 2: always += speed
                // -> speed is 0 if we just started after idle, so always use max
                //    for best results
                float speed = Mathf.Max(start.movementSpeed, goal.movementSpeed);
                return Vector3.MoveTowards(currentPosition, goal.localPosition, speed * Time.deltaTime);
            }
            return currentPosition;
        }

        static Quaternion InterpolateRotation(DataPoint start, DataPoint goal, Quaternion defaultRotation)
        {
            if (start.localRotation != null)
            {
                float t = CurrentInterpolationFactor(start, goal);
                return Quaternion.Slerp(start.localRotation, goal.localRotation, t);
            }
            return defaultRotation;
        }

        static Vector3 InterpolateScale(DataPoint start, DataPoint goal, Vector3 currentScale)
        {
            if (start.localScale != null)
            {
                float t = CurrentInterpolationFactor(start, goal);
                return Vector3.Lerp(start.localScale, goal.localScale, t);
            }
            return currentScale;
        }

        static float CurrentInterpolationFactor(DataPoint start, DataPoint goal)
        {
            if (start.timeStamp != 0)
            {
                float difference = goal.timeStamp - start.timeStamp;

                // the moment we get 'goal', 'start' is supposed to
                // start, so elapsed time is based on:
                float elapsed = Time.time - goal.timeStamp;
                // avoid NaN
                return difference > 0 ? elapsed / difference : 1;
            }
            return 1;
        }

        #region Debug Gizmos

        // draw the data points for easier debugging
        void OnDrawGizmos()
        {
            // draw start and goal points
            if (start.localPosition != null) DrawDataPointGizmo(start, Color.gray);
            if (goal.localPosition != null) DrawDataPointGizmo(goal, Color.white);

            // draw line between them
            if (start.localPosition != null && goal.localPosition != null) DrawLineBetweenDataPoints(start, goal, Color.cyan);
        }

        static void DrawDataPointGizmo(DataPoint data, Color color)
        {
            // use a little offset because transform.localPosition might be in
            // the ground in many cases
            Vector3 offset = Vector3.up * 0.01f;

            // draw position
            Gizmos.color = color;
            Gizmos.DrawSphere(data.localPosition + offset, 0.5f);

            // draw forward and up
            // like unity move tool
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(data.localPosition + offset, data.localRotation * Vector3.forward);

            // like unity move tool
            Gizmos.color = Color.green;
            Gizmos.DrawRay(data.localPosition + offset, data.localRotation * Vector3.up);
        }

        static void DrawLineBetweenDataPoints(DataPoint data1, DataPoint data2, Color color)
        {
            Gizmos.color = color;
            Gizmos.DrawLine(data1.localPosition, data2.localPosition);
        }

        #endregion

        #region Start & Stop Callbacks

        /// <summary>
        /// This is invoked for NetworkBehaviour objects when they become active on the server.
        /// <para>This could be triggered by NetworkServer.Listen() for objects in the scene, or by NetworkServer.Spawn() for objects that are dynamically created.</para>
        /// <para>This will be called for objects on a "host" as well as for object on a dedicated server.</para>
        /// </summary>
        public override void OnStartServer() { }

        /// <summary>
        /// Invoked on the server when the object is unspawned
        /// <para>Useful for saving object data in persistant storage</para>
        /// </summary>
        public override void OnStopServer() { }

        /// <summary>
        /// Called on every NetworkBehaviour when it is activated on a client.
        /// <para>Objects on the host have this function called, as there is a local client on the host. The values of SyncVars on object are guaranteed to be initialized correctly with the latest state from the server when this function is called on the client.</para>
        /// </summary>
        public override void OnStartClient() { }

        /// <summary>
        /// This is invoked on clients when the server has caused this object to be destroyed.
        /// <para>This can be used as a hook to invoke effects or do client specific cleanup.</para>
        /// </summary>
        public override void OnStopClient() { }

        /// <summary>
        /// Called when the local player object has been set up.
        /// <para>This happens after OnStartClient(), as it is triggered by an ownership message from the server. This is an appropriate place to activate components or functionality that should only be active for the local player, such as cameras and input.</para>
        /// </summary>
        public override void OnStartLocalPlayer() { }

        /// <summary>
        /// This is invoked on behaviours that have authority, based on context and <see cref="NetworkIdentity.hasAuthority">NetworkIdentity.hasAuthority</see>.
        /// <para>This is called after <see cref="OnStartServer">OnStartServer</see> and before <see cref="OnStartClient">OnStartClient.</see></para>
        /// <para>When <see cref="NetworkIdentity.AssignClientAuthority"/> is called on the server, this will be called on the client that owns the object. When an object is spawned with <see cref="NetworkServer.Spawn">NetworkServer.Spawn</see> with a NetworkConnection parameter included, this will be called on the client that owns the object.</para>
        /// </summary>
        public override void OnStartAuthority() { }

        /// <summary>
        /// This is invoked on behaviours when authority is removed.
        /// <para>When NetworkIdentity.RemoveClientAuthority is called on the server, this will be called on the client that owns the object.</para>
        /// </summary>
        public override void OnStopAuthority() { }

        #endregion
    }
}