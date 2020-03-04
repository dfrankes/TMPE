namespace TrafficManager.UI.SubTools {
    using ColossalFramework.Math;
    using ColossalFramework;
    using CSUtil.Commons;
    using JetBrains.Annotations;
    using System.Collections.Generic;
    using System.Linq;
    using TrafficManager.Manager.Impl;
    using TrafficManager.State.ConfigData;
    using TrafficManager.State.Keybinds;
    using TrafficManager.State;
    using TrafficManager.Util.Caching;
    using UnityEngine;
    using TrafficManager.Util;
    using TrafficManager.UI.Helpers;
    using static TrafficManager.Util.Shortcuts;
    using System;
    using GenericGameBridge.Service;

    public class LaneConnectorTool : SubTool {
        private enum SelectionMode {
            None,
            SelectSource,
            SelectTarget
        }

        public enum StayInLaneMode {
            None,
            Both,
            Forward,
            Backward
        }

        private static bool verbose => true | DebugSwitch.LaneConnections.Get();
        private static readonly Color DefaultLaneEndColor = new Color(1f, 1f, 1f, 0.4f);
        private LaneEnd selectedLaneEnd;
        private LaneEnd hoveredLaneEnd;
        private readonly Dictionary<ushort, List<LaneEnd>> currentLaneEnds;
        private StayInLaneMode stayInLaneMode = StayInLaneMode.None;
        // private bool initDone = false;

        /// <summary>Unity frame when OnGui detected the shortcut for Stay in Lane.
        /// Resets when the event is consumed or after a few frames.</summary>
        private int frameStayInLanePressed;

        /// <summary>Clear lane lines is Delete/Backspace (configurable)</summary>
        private int frameClearPressed;

        /// <summary>
        /// Stores potentially visible ids for nodes while the camera did not move
        /// </summary>
        private GenericArrayCache<ushort> CachedVisibleNodeIds { get; }

        /// <summary>
        /// Stores last cached camera position in <see cref="CachedVisibleNodeIds"/>
        /// </summary>
        private CameraTransformValue LastCachedCamera { get; set; }

        private class LaneEnd {
            internal ushort SegmentId;
            internal ushort NodeId;
            internal bool StartNode;
            internal uint LaneId;
            internal bool IsSource;
            internal bool IsTarget;
            internal int OuterSimilarLaneIndex;
            internal int InnerSimilarLaneIndex; /// used for stay in lane.
            internal int SegmentIndex; /// index accesable by NetNode.GetSegment(SegmentIndex);
            internal readonly List<LaneEnd> ConnectedLaneEnds = new List<LaneEnd>();
            internal Color Color;

            internal SegmentLaneMarker SegmentMarker;
            internal NodeLaneMarker NodeMarker;

            internal NetInfo.LaneType LaneType;
            internal VehicleInfo.VehicleType VehicleType;

            /// <summary>
            ///  Intersects mouse ray with marker bounds.
            /// </summary>
            /// <returns><c>true</c>if mouse ray intersects with marker <c>false</c> otherwise</returns>
            internal bool IntersectRay() => SegmentMarker.IntersectRay();

            /// <summary>
            /// renders lane overlay. If highlighted, renders englarged sheath(lane+circle) overlay. Otherwise
            /// renders circle at lane end.
            /// </summary>
            internal void RenderOverlay(RenderManager.CameraInfo cameraInfo, Color color, bool highlight = false) {
                if (highlight) {
                    SegmentMarker.RenderOverlay(cameraInfo, color, enlarge: true);
                }
                NodeMarker.RenderOverlay(cameraInfo, color, enlarge: highlight);
            }
        }

        public LaneConnectorTool(TrafficManagerTool mainTool)
            : base(mainTool) {
            // Log._Debug($"LaneConnectorTool: Constructor called");
            currentLaneEnds = new Dictionary<ushort, List<LaneEnd>>();

            CachedVisibleNodeIds = new GenericArrayCache<ushort>(NetManager.MAX_NODE_COUNT);
            LastCachedCamera = new CameraTransformValue();
        }

        public override void OnToolGUI(Event e) {
            // Log._Debug(
            //    $"LaneConnectorTool: OnToolGUI. SelectedNodeId={SelectedNodeId} " +
            //    $"SelectedSegmentId={SelectedSegmentId} HoveredNodeId={HoveredNodeId} " +
            //    $"HoveredSegmentId={HoveredSegmentId} IsInsideUI={MainTool.GetToolController().IsInsideUI}");
            if (KeybindSettingsBase.LaneConnectorStayInLane.IsPressed(e)) {
                frameStayInLanePressed = Time.frameCount;

                // this will be consumed in RenderOverlay() if the key was pressed
                // not too long ago (within 20 Unity frames or 0.33 sec)
            }

            if (KeybindSettingsBase.LaneConnectorDelete.IsPressed(e)) {
                frameClearPressed = Time.frameCount;

                // this will be consumed in RenderOverlay() if the key was pressed
                // not too long ago (within 20 Unity frames or 0.33 sec)
            }
        }

        public override void RenderInfoOverlay(RenderManager.CameraInfo cameraInfo) {
            ShowOverlay(true, cameraInfo);
        }

        private void ShowOverlay(bool viewOnly, RenderManager.CameraInfo cameraInfo) {
            if (viewOnly && !(Options.connectedLanesOverlay ||
                PrioritySignsTool.MassEditOVerlay.IsActive)) {
                return;
            }

            NetManager netManager = Singleton<NetManager>.instance;

            Vector3 camPos = Singleton<SimulationManager>.instance.m_simulationView.m_position;

            // Bounds bounds = new Bounds(Vector3.zero, Vector3.one);
            Camera currentCamera = Camera.main;

            // Check if camera pos/angle has changed then re-filter the visible nodes
            // Assumption: The states checked in this loop don't change while the tool is active
            var currentCameraState = new CameraTransformValue(currentCamera);
            if (!LastCachedCamera.Equals(currentCameraState)) {
                CachedVisibleNodeIds.Clear();
                LastCachedCamera = currentCameraState;

                for (ushort nodeId = 1; nodeId < NetManager.MAX_NODE_COUNT; ++nodeId) {
                    if (!Constants.ServiceFactory.NetService.IsNodeValid(nodeId)) {
                        continue;
                    }

                    //---------------------------
                    // Check the connection class
                    //---------------------------
                    // TODO refactor connection class check
                    ItemClass connectionClass =
                        NetManager.instance.m_nodes.m_buffer[nodeId].Info.GetConnectionClass();

                    if ((connectionClass == null) ||
                        !((connectionClass.m_service == ItemClass.Service.Road) ||
                          ((connectionClass.m_service == ItemClass.Service.PublicTransport) &&
                           ((connectionClass.m_subService == ItemClass.SubService.PublicTransportTrain) ||
                            (connectionClass.m_subService == ItemClass.SubService.PublicTransportMetro) ||
                            (connectionClass.m_subService == ItemClass.SubService.PublicTransportMonorail))))) {
                        continue;
                    }

                    //--------------------------
                    // Check the camera distance
                    //--------------------------
                    Vector3 diff = NetManager.instance.m_nodes.m_buffer[nodeId].m_position - camPos;

                    if (diff.sqrMagnitude > TrafficManagerTool.MAX_OVERLAY_DISTANCE_SQR) {
                        continue; // do not draw if too distant
                    }

                    // Add
                    CachedVisibleNodeIds.Add(nodeId);
                }
            }

            for (int cacheIndex = CachedVisibleNodeIds.Size - 1; cacheIndex >= 0; cacheIndex--) {
                var nodeId = CachedVisibleNodeIds.Values[cacheIndex];

                bool hasMarkers = currentLaneEnds.TryGetValue((ushort)nodeId, out List<LaneEnd> laneEnds);
                if (!viewOnly && (GetSelectionMode() == SelectionMode.None)) {
                    MainTool.DrawNodeCircle(
                        cameraInfo,
                        (ushort)nodeId,
                        DefaultLaneEndColor,
                        false);
                }

                if (!hasMarkers) {
                    continue;
                }

                foreach (LaneEnd laneEnd in laneEnds) {
                    if (!Constants.ServiceFactory.NetService.IsLaneValid(laneEnd.LaneId)) {
                        continue;
                    }

                    if (laneEnd != selectedLaneEnd) {
                        foreach (LaneEnd targetLaneEnd in laneEnd.ConnectedLaneEnds) {
                            // render lane connection from laneEnd to targetLaneEnd
                            if (!Constants.ServiceFactory.NetService.IsLaneValid(targetLaneEnd.LaneId)) {
                                continue;
                            }

                            DrawLaneCurve(
                                cameraInfo,
                                laneEnd.NodeMarker.TerrainPosition,
                                targetLaneEnd.NodeMarker.TerrainPosition,
                                NetManager.instance.m_nodes.m_buffer[nodeId].m_position,
                                laneEnd.Color,
                                Color.black);
                        }
                    }

                    if (viewOnly || (nodeId != SelectedNodeId)) {
                        continue;
                    }

                    bool drawMarker = false;
                    bool SourceMode = GetSelectionMode() == SelectionMode.SelectSource;
                    bool TargetMode = GetSelectionMode() == SelectionMode.SelectTarget;
                    if ( SourceMode & laneEnd.IsSource) {
                        // draw source marker in source selection mode,
                        // make exception for markers that have no target:
                        foreach(var targetLaneEnd in laneEnds) {
                            if (CanConnect(laneEnd, targetLaneEnd)){
                                drawMarker = true;
                                break;
                            }
                        }
                    } else if (TargetMode) {
                        // selected source marker in target selection mode
                        drawMarker =
                            selectedLaneEnd == laneEnd ||
                            CanConnect(selectedLaneEnd, laneEnd);
                    }

                    // highlight hovered marker and selected marker
                    if (drawMarker) {
                        bool markerIsHovered = false;
                        if (hoveredLaneEnd == null) {
                            float hitH = TrafficManagerTool.GetAccurateHitHeight();
                            markerIsHovered =
                                laneEnd.IntersectRay();

                            if (markerIsHovered) {
                                hoveredLaneEnd = laneEnd;
                            }
                        }

                        bool isTarget = selectedLaneEnd != null && laneEnd != selectedLaneEnd;
                        var color = isTarget ? Color.white : laneEnd.Color;
                        bool highlightMarker = laneEnd == selectedLaneEnd || markerIsHovered;
                        laneEnd.RenderOverlay(cameraInfo, color, highlightMarker);
                    } // if drawMarker

                    if (selectedLaneEnd != null) {
                        // lane curves for selectedMarker will be drawn last to
                        // be on the top of other lane markers.
                        foreach (LaneEnd targetLaneEnd in selectedLaneEnd.ConnectedLaneEnds) {
                            if (!Constants.ServiceFactory.NetService.IsLaneValid(targetLaneEnd.LaneId)) {
                                continue;
                            }

                            DrawLaneCurve(
                                cameraInfo,
                                selectedLaneEnd.NodeMarker.TerrainPosition,
                                targetLaneEnd.NodeMarker.TerrainPosition,
                                NetManager.instance.m_nodes.m_buffer[nodeId].m_position,
                                selectedLaneEnd.Color,
                                Color.grey,
                                size: 0.18f // Embolden
                                );
                        } // end foreach selectedMarker.ConnectedMarkers
                    } // end if selectedMarker != null
                } // end foreach lanemarker in node markers
            } // end for node in all nodes
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo) {
            // Log._Debug($"LaneConnectorTool: RenderOverlay. SelectedNodeId={SelectedNodeId}
            //     SelectedSegmentId={SelectedSegmentId} HoveredNodeId={HoveredNodeId}
            //     HoveredSegmentId={HoveredSegmentId} IsInsideUI={MainTool.GetToolController().IsInsideUI}");

            // draw lane markers and connections
            hoveredLaneEnd = null;

            ShowOverlay(false, cameraInfo);

            // draw bezier from source marker to mouse position in target marker selection
            if (SelectedNodeId != 0) {
                if (GetSelectionMode() == SelectionMode.SelectTarget) {
                    Vector3 selNodePos =
                        NetManager.instance.m_nodes.m_buffer[SelectedNodeId].m_position;

                    // Draw a currently dragged curve
                    var pos = HitPos;
                    if (hoveredLaneEnd == null) {
                        float hitH = TrafficManagerTool.GetAccurateHitHeight();
                        pos.y = hitH; // fix height.
                        float mouseH = MousePosition.y;
                        if (hitH < mouseH - TrafficManagerTool.MAX_HIT_ERROR) {
                            // for metros lane curve is projected on the ground.
                            pos = MousePosition;
                        }
                    } else {
                        // snap to hovered:
                        pos = hoveredLaneEnd.NodeMarker.TerrainPosition;
                    }
                    DrawLaneCurve(
                        cameraInfo,
                        selectedLaneEnd.NodeMarker.TerrainPosition,
                        pos,
                        selNodePos,
                        Color.Lerp(selectedLaneEnd.Color, Color.white, 0.33f),
                        Color.white,
                        size:0.11f);

                }

                NetNode[] nodesBuffer = Singleton<NetManager>.instance.m_nodes.m_buffer;

                if ((frameClearPressed > 0) && ((Time.frameCount - frameClearPressed) < 20)) {
                    // 0.33 sec
                    frameClearPressed = 0; // consumed
                    // remove all connections at selected node
                    LaneConnectionManager.Instance.RemoveLaneConnectionsFromNode(SelectedNodeId);
                    RefreshCurrentNodeMarkers(SelectedNodeId);
                }

                // Must press Shift+S (or another shortcut) within last 20 frames for this to work
                bool quickSetup = (frameStayInLanePressed > 0)
                                 && ((Time.frameCount - frameStayInLanePressed) < 20); // 0.33 sec
                if (quickSetup) {
                    frameStayInLanePressed = 0; // not pressed anymore (consumed)
                    frameClearPressed = 0; // consumed
                    selectedLaneEnd = null;
                    ref NetNode node = ref SelectedNodeId.ToNode();

                    bool stayInLane = GetSoredtedSegments(SelectedNodeId, out List<ushort> segList) ;
                    bool oneway = segMan.CalculateIsOneWay(segList[0]) || segMan.CalculateIsOneWay(segList[1]);

                    if (stayInLane) {
                        // "stay in lane"
                        switch (stayInLaneMode) {
                            case StayInLaneMode.None: {
                                    stayInLaneMode = !oneway? StayInLaneMode.Both:StayInLaneMode.Forward;
                                    break;
                                }

                            case StayInLaneMode.Both: {
                                    stayInLaneMode = StayInLaneMode.Forward;
                                    break;
                                }

                            case StayInLaneMode.Forward: {
                                    stayInLaneMode = !oneway ? StayInLaneMode.Backward : StayInLaneMode.None;
                                    break;
                                }

                            case StayInLaneMode.Backward: {
                                    stayInLaneMode = StayInLaneMode.None;
                                    break;
                                }
                        }
                        MainTool.Guide.Deactivate("LaneConnectorTool:quick-setup is not supported for this junction setup.");
                    } else {
                        MainTool.Guide.Activate("LaneConnectorTool:quick-setup is not supported for this junction setup.");
                    }

                    Log._Debug($"stayInLane:{stayInLane} stayInLaneMode:{stayInLaneMode}\n" +
                        $"GetMarkerSelectionMode()={GetSelectionMode()} SelectedNodeId={SelectedNodeId}");

                    if (stayInLane) {
                        StayInLane(SelectedNodeId, stayInLaneMode);
                        RefreshCurrentNodeMarkers(SelectedNodeId);
                    } // end if stay in lane
                } // end if quick setup
            } // end if selected node

            if ((GetSelectionMode() == SelectionMode.None) && (HoveredNodeId != 0)) {
                // draw hovered node
                MainTool.DrawNodeCircle(cameraInfo, HoveredNodeId, Input.GetMouseButton(0));
            }
        }

        public static bool GetSoredtedSegments(ushort nodeId, out List<ushort> segments) {
            segments = PriorityRoad.GetNodeSegments(nodeId);
            bool ret = false;
            int n = segments.Count;
            if (n == 2) {
                segments.Add(0);
                segments.Add(0);
                ret = true;
            } else if (n == 3) {
                if (!PriorityRoad.ArrangeT(segments)) {
                    segments.Sort(PriorityRoad.CompareSegments);
                }

                // Prevent confusion if all roads are the same.
                ret = PriorityRoad.CompareSegments(segments[1], segments[2]) != 0;

                // same sized one way roads are supported.
                ret |= segMan.CalculateIsOneWay(segments[0]) && segMan.CalculateIsOneWay(segments[1]);

                segments.Add(0);
            } else if(n == 4) {
                segments.Sort(PriorityRoad.CompareSegments);

                // Prevent confusion if all roads are the same.
                ret = PriorityRoad.CompareSegments(segments[1], segments[2]) != 0;
            }

            return ret;
        }

        /// <summary>
        /// connects lanes in a T junction such that each lane is connected to one other lane.
        /// lane arithmatic must work for the side of the road which has a segment connection.
        /// </summary>
        /// <param name="nodeId"></param>
        /// <param name="mode">determines for which side to connect lanes.</param>
        /// <returns>if lane arithmatic works (when <paramref name="checkOnly"/> is <c>true</c>) </returns>
        public static void StayInLane(ushort nodeId, StayInLaneMode mode = StayInLaneMode.None) {
            LaneConnectionManager.Instance.RemoveLaneConnectionsFromNode(nodeId);

            GetSoredtedSegments(nodeId, out List<ushort> segments);
            ushort mainAgainst, mainToward;
            ref NetNode node = ref nodeId.ToNode();
            ushort minor1 = segments[2];
            ushort minor2 = 0;

            if (segments[2] != 0) {
                ref NetSegment segment2 = ref segments[2].ToSegment();
                mainAgainst = segment2.GetNearSegment(nodeId);
                mainToward = segment2.GetFarSegment(nodeId);
                bool oneway0 = segMan.CalculateIsOneWay(mainAgainst);
                bool oneway1 = segMan.CalculateIsOneWay(mainToward);
                bool oneway = oneway0 && oneway1;
                bool connectedFromInside =
                    oneway && netService.GetHeadNode(mainAgainst) == nodeId;
                if (oneway) {
                    minor2 = segments[3];
                }
                if (connectedFromInside) {
                    Swap(ref mainToward, ref mainAgainst);
                    Swap(ref minor1, ref minor2);
                }
            } else {
                mainToward = segments[0];
                mainAgainst = segments[1];
            }
            if (mode == StayInLaneMode.Both || mode == StayInLaneMode.Forward) {
                StayInLane(nodeId, mainToward, mainAgainst, minor1, minor2);
            }
            if(mode == StayInLaneMode.Both || mode == StayInLaneMode.Backward) {
                StayInLane(nodeId, mainAgainst, mainToward, segments[3], 0);
            }
        }

        private static void StayInLane(ushort nodeId, ushort mainTowardId, ushort mainAgainstId, ushort minorId, ushort minor2Id) {
            Log._Debug($"StayInLane(nodeId:{nodeId}, " +
                $"mainTowardId:{mainTowardId}, mainAgainstId:{mainAgainstId}, " +
                $"minorId:{minorId}, minor2Id:{minor2Id})");

            ref NetSegment segment = ref minorId.ToSegment();
            ref NetNode node = ref nodeId.ToNode();
            bool oneway0 = segMan.CalculateIsOneWay(mainAgainstId);
            bool oneway1 = segMan.CalculateIsOneWay(mainTowardId);
            bool oneway = oneway0 && oneway1;

            // which lanes should split/merge in case of lane mismatch:
            bool splitMiddle = oneway && minorId !=0 && minor2Id != 0;
            bool splitOuter = oneway && minorId != 0 && minor2Id == 0;
            bool splitInner = !splitMiddle && !splitOuter;

            int nMinorToward = minorId == 0 ? 0 : PriorityRoad.CountLanesTowardJunction(minorId, nodeId);
            int nMinorAgainst = minorId == 0 ? 0 : PriorityRoad.CountLanesAgainstJunction(minorId, nodeId); 
            int nMinor2Toward = minor2Id == 0 ? 0 : PriorityRoad.CountLanesTowardJunction(minor2Id, nodeId); 
            int nMinor2Against = minor2Id == 0 ? 0 : PriorityRoad.CountLanesAgainstJunction(minor2Id, nodeId); 
            int nMainToward = PriorityRoad.CountLanesTowardJunction(mainTowardId, nodeId); 
            int nMainAgainst = PriorityRoad.CountLanesAgainstJunction(mainAgainstId, nodeId); 
            int totalToward = nMinorToward + nMainToward + nMinor2Toward;
            int totalAgainst = nMinorAgainst + nMainAgainst +nMinor2Against;

            bool laneArithmaticWorks = totalToward == totalAgainst && nMainToward == nMinorAgainst;
            Log._Debug($"StayInLane: nMinorToward={nMinorToward} nMinorAgainst={nMinorAgainst} nMainToward={nMainAgainst} nMainAgainst={nMainAgainst} " +
                $"nMinor2Toward={nMinor2Toward} nMinor2Against={nMinor2Against} totalToward={totalToward} totalAgainst={totalAgainst}");

            if (totalToward <= 1 || totalAgainst <= 1) {
                return; // No lane connections are necessry.
            }

            float ratio = totalAgainst/ (float)totalToward;
            bool IndexesMatch(int sourceIdx, int targetIdx) =>
                ratio >= 1 ?
                IndexesMatchHelper(sourceIdx, targetIdx, ratio ) :
                IndexesMatchHelper(targetIdx, sourceIdx, (1f / ratio));

            int LowerBound(int idx, float r) => Round(idx * r);
            int UpperBound(int idx, float r) => Round((idx + 1) * r);

            bool IndexesMatchHelper(int idx1, int idx2, float r) =>
                InBound(LowerBound(idx1, r), UpperBound(idx1, r), idx2);

            // checks if value is in [InclusiveLowerBound,ExclusiveUpperBound)
            bool InBound(int InclusiveLowerBound, int ExclusiveUpperBound, int value) =>
                InclusiveLowerBound <= value && value < ExclusiveUpperBound;

            if (verbose) {
                for (int i = 0; i < totalToward; ++i)
                    for (int j = 0; j < totalAgainst; ++j)
                        Log._Debug($"ratio:{ratio} sourceIdx:{i}=>" +
                            $"bounds=[{Mathf.FloorToInt(i * ratio)},{Mathf.FloorToInt((i + 1) * ratio)})=>" +
                            $"IndexesMatch({i},{j})={IndexesMatch(i, j)}");
            }

            const float EPSILON = 1e-10f;
            // rounding approach helps us control which lanes will split/merge in case of lane mismatch.
            int Round(float f) {
                if (splitInner)
                    return Mathf.FloorToInt(f + EPSILON);
                else if (splitOuter)
                    return Mathf.CeilToInt(f - EPSILON);
                else
                    return Mathf.RoundToInt(f);
            }

            List<LaneEnd> laneEnds = GetLaneEnds(nodeId, ref node);
            foreach (LaneEnd sourceLaneEnd in laneEnds) {
                if (!sourceLaneEnd.IsSource ||
                    sourceLaneEnd.SegmentId == mainAgainstId) {
                    continue;
                }
                foreach (LaneEnd targetLaneEnd in laneEnds) {
                    if (!targetLaneEnd.IsTarget ||
                        targetLaneEnd.SegmentId == sourceLaneEnd.SegmentId ||
                        targetLaneEnd.SegmentId == mainTowardId ||
                        !CanConnect(sourceLaneEnd, targetLaneEnd)
                        ) {
                        continue;
                    }
                    bool connect = false;
                    if (
                        sourceLaneEnd.SegmentId == mainTowardId &&
                        targetLaneEnd.SegmentId == minorId &&
                        IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex, targetLaneEnd.OuterSimilarLaneIndex)) {
                        connect = true;
                    } else if (
                        sourceLaneEnd.SegmentId == minorId &&
                        targetLaneEnd.SegmentId == mainAgainstId &&
                        IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex, targetLaneEnd.OuterSimilarLaneIndex)) {
                        connect = true;
                    } else if (
                        sourceLaneEnd.SegmentId == mainTowardId &&
                        targetLaneEnd.SegmentId == mainAgainstId &&
                        //TODO [issue does not exist] replace with subtraction from bound.
                        !IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex, nMinorAgainst-1) &&
                        !IndexesMatch(targetLaneEnd.OuterSimilarLaneIndex, nMinorToward-1) &&
                        IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex + nMinorToward, targetLaneEnd.OuterSimilarLaneIndex + nMinorAgainst)
                        ) {
                        connect = true;
                    } else if (
                        sourceLaneEnd.SegmentId == mainTowardId &&
                        targetLaneEnd.SegmentId == minor2Id &&
                        !IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex, nMinorAgainst - 1) &&
                        !IndexesMatch(targetLaneEnd.OuterSimilarLaneIndex, nMinorToward + nMainToward - 1) &&
                        IndexesMatch(
                            sourceLaneEnd.OuterSimilarLaneIndex + nMinorToward,
                            targetLaneEnd.OuterSimilarLaneIndex + nMinorAgainst + nMainAgainst)) {
                        connect = true;
                    } else if (
                        sourceLaneEnd.SegmentId == minor2Id &&
                        targetLaneEnd.SegmentId == mainAgainstId &&
                        !IndexesMatch(sourceLaneEnd.OuterSimilarLaneIndex, nMinorAgainst + nMainAgainst - 1) &&
                        !IndexesMatch(targetLaneEnd.OuterSimilarLaneIndex, nMinorToward - 1) &&
                        IndexesMatch(
                            sourceLaneEnd.OuterSimilarLaneIndex + nMinorToward + nMainToward,
                            targetLaneEnd.OuterSimilarLaneIndex + nMinorAgainst)) {
                        connect = true;
                    }

                    if (connect) {
                        Log._Debug(
                            $"Adding lane connection {sourceLaneEnd.LaneId} -> " +
                            $"{targetLaneEnd.LaneId}");
                        LaneConnectionManager.Instance.AddLaneConnection(
                            sourceLaneEnd.LaneId,
                            targetLaneEnd.LaneId,
                            sourceLaneEnd.StartNode);
                    }
                } // foreach
            } // foreach
        }

        public override void OnPrimaryClickOverlay() {
#if DEBUG
            bool logLaneConn = DebugSwitch.LaneConnections.Get();
#else
            const bool logLaneConn = false;
#endif
            Log._DebugIf(
                logLaneConn,
                () => $"LaneConnectorTool: OnPrimaryClickOverlay. SelectedNodeId={SelectedNodeId} " +
                $"SelectedSegmentId={SelectedSegmentId} HoveredNodeId={HoveredNodeId} " +
                $"HoveredSegmentId={HoveredSegmentId}");

            if (IsCursorInPanel()) {
                return;
            }

            if (GetSelectionMode() == SelectionMode.None) {
                if (HoveredNodeId != 0) {
                    Log._DebugIf(
                        logLaneConn,
                        () => "LaneConnectorTool: HoveredNode != 0");

                    if (NetManager.instance.m_nodes.m_buffer[HoveredNodeId].CountSegments() < 2) {
                        // this node cannot be configured (dead end)
                        Log._DebugIf(
                            logLaneConn,
                            () => "LaneConnectorTool: Node is a dead end");

                        SelectedNodeId = 0;
                        selectedLaneEnd = null;
                        stayInLaneMode = StayInLaneMode.None;
                        return;
                    }

                    if (SelectedNodeId != HoveredNodeId) {
                        Log._DebugIf(
                            logLaneConn,
                            () => $"Node {HoveredNodeId} has been selected. Creating markers.");

                        // selected node has changed. create markers
                        List<LaneEnd> laneEnds = GetLaneEnds(
                            HoveredNodeId,
                            ref Singleton<NetManager>.instance.m_nodes.m_buffer[HoveredNodeId]);

                        if (laneEnds != null) {
                            SelectedNodeId = HoveredNodeId;
                            selectedLaneEnd = null;
                            stayInLaneMode = StayInLaneMode.None;

                            currentLaneEnds[SelectedNodeId] = laneEnds;
                        }

                        // this.allNodeMarkers[SelectedNodeId] = GetNodeMarkers(SelectedNodeId);
                    }
                } else {
                    Log._DebugIf(
                        logLaneConn,
                        () => $"LaneConnectorTool: Node {SelectedNodeId} has been deselected.");

                    // click on free spot. deselect node
                    SelectedNodeId = 0;
                    selectedLaneEnd = null;
                    stayInLaneMode = StayInLaneMode.None;
                    return;
                }
            }

            if (hoveredLaneEnd == null) {
                return;
            }

            //-----------------------------------
            // Hovered Marker
            //-----------------------------------
            stayInLaneMode = StayInLaneMode.None;

            Log._DebugIf(
                logLaneConn,
                () => $"LaneConnectorTool: hoveredMarker != null. selMode={GetSelectionMode()}");

            // hovered marker has been clicked
            if (GetSelectionMode() == SelectionMode.SelectSource) {
                // select source marker
                selectedLaneEnd = hoveredLaneEnd;
                Log._DebugIf(
                    logLaneConn,
                    () => "LaneConnectorTool: set selected marker");
            } else if (GetSelectionMode() == SelectionMode.SelectTarget) {
                // select target marker
                // bool success = false;
                if (LaneConnectionManager.Instance.RemoveLaneConnection(
                    selectedLaneEnd.LaneId,
                    hoveredLaneEnd.LaneId,
                    selectedLaneEnd.StartNode)) {

                    // try to remove connection
                    selectedLaneEnd.ConnectedLaneEnds.Remove(hoveredLaneEnd);
                    Log._DebugIf(
                        logLaneConn,
                        () => $"LaneConnectorTool: removed lane connection: {selectedLaneEnd.LaneId}, " +
                        $"{hoveredLaneEnd.LaneId}");

                    // success = true;
                } else if (LaneConnectionManager.Instance.AddLaneConnection(
                    selectedLaneEnd.LaneId,
                    hoveredLaneEnd.LaneId,
                    selectedLaneEnd.StartNode)) {
                    // try to add connection
                    selectedLaneEnd.ConnectedLaneEnds.Add(hoveredLaneEnd);
                    Log._DebugIf(
                        logLaneConn,
                        () => $"LaneConnectorTool: added lane connection: {selectedLaneEnd.LaneId}, " +
                        $"{hoveredLaneEnd.LaneId}");

                    // success = true;
                }

                /*if (success) {
                            // connection has been modified. switch back to source marker selection
                            Log._Debug($"LaneConnectorTool: switch back to source marker selection");
                            selectedMarker = null;
                            selMode = MarkerSelectionMode.SelectSource;
                    }*/
            }
        }

        public override void OnSecondaryClickOverlay() {
#if DEBUG
            bool logLaneConn = DebugSwitch.LaneConnections.Get();
#else
            const bool logLaneConn = false;
#endif

            if (IsCursorInPanel()) {
                return;
            }

            switch (GetSelectionMode()) {
                // also: case MarkerSelectionMode.None:
                default: {
                        Log._DebugIf(
                            logLaneConn,
                            () => "LaneConnectorTool: OnSecondaryClickOverlay: nothing to do");
                        stayInLaneMode = StayInLaneMode.None;
                        break;
                    }

                case SelectionMode.SelectSource: {
                        // deselect node
                        Log._DebugIf(
                            logLaneConn,
                            () => "LaneConnectorTool: OnSecondaryClickOverlay: selected node id = 0");
                        SelectedNodeId = 0;
                        break;
                    }

                case SelectionMode.SelectTarget: {
                        // deselect source marker
                        Log._DebugIf(
                            logLaneConn,
                            () => "LaneConnectorTool: OnSecondaryClickOverlay: switch to selected source mode");
                        selectedLaneEnd = null;
                        break;
                    }
            }
        }

        public override void OnActivate() {
#if DEBUG
            bool logLaneConn = DebugSwitch.LaneConnections.Get();
            if (logLaneConn) {
                Log._Debug("LaneConnectorTool: OnActivate");
            }
#endif
            SelectedNodeId = 0;
            selectedLaneEnd = null;
            hoveredLaneEnd = null;
            stayInLaneMode = StayInLaneMode.None;
            RefreshCurrentNodeMarkers();
        }

        private void RefreshCurrentNodeMarkers(ushort forceNodeId = 0) {
            if (forceNodeId == 0) {
                currentLaneEnds.Clear();
            } else {
                currentLaneEnds.Remove(forceNodeId);
            }

            for (ushort nodeId = forceNodeId == 0 ? (ushort)1 : forceNodeId;
                 nodeId <= (forceNodeId == 0 ? NetManager.MAX_NODE_COUNT - 1 : forceNodeId);
                 ++nodeId) {
                if (!Constants.ServiceFactory.NetService.IsNodeValid(nodeId)) {
                    continue;
                }

                if (nodeId != SelectedNodeId &&
                    !LaneConnectionManager.Instance.HasNodeConnections(nodeId)) {
                    continue;
                }

                List<LaneEnd> laneEnds = GetLaneEnds(
                    nodeId,
                    ref Singleton<NetManager>.instance.m_nodes.m_buffer[nodeId]);

                if (laneEnds == null) {
                    continue;
                }

                currentLaneEnds[nodeId] = laneEnds;
            }
        }

        private SelectionMode GetSelectionMode() {
            if (SelectedNodeId == 0) {
                return SelectionMode.None;
            }

            return selectedLaneEnd == null
                       ? SelectionMode.SelectSource
                       : SelectionMode.SelectTarget;
        }

        public override void Cleanup() { }

        public override void Initialize() {
            base.Initialize();
            Cleanup();
            if (Options.connectedLanesOverlay ||
                PrioritySignsTool.MassEditOVerlay.IsActive) {
                RefreshCurrentNodeMarkers();
            } else {
                currentLaneEnds.Clear();
            }
        }

        private static List<LaneEnd> GetLaneEnds(ushort nodeId, ref NetNode node) {
            if (nodeId == 0) {
                return null;
            }

            if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None) {
                return null;
            }

            List<LaneEnd> laneEnds = new List<LaneEnd>();
            int nodeMarkerColorIndex = 0;
            LaneConnectionManager connManager = LaneConnectionManager.Instance;

            int offsetMultiplier = node.CountSegments() <= 2 ? 3 : 1;

            for (int i = 0; i < 8; i++) {
                ushort segmentId = node.GetSegment(i);

                if (segmentId == 0) {
                    continue;
                }

                NetSegment[] segmentsBuffer = NetManager.instance.m_segments.m_buffer;
                bool startNode = segmentsBuffer[segmentId].m_startNode == nodeId;
                Vector3 offset = segmentsBuffer[segmentId]
                                     .FindDirection(segmentId, nodeId) * offsetMultiplier;
                NetInfo.Lane[] lanes = segmentsBuffer[segmentId].Info.m_lanes;
                uint laneId = segmentsBuffer[segmentId].m_lanes;

                for (byte laneIndex = 0; (laneIndex < lanes.Length) && (laneId != 0); laneIndex++) {
                    NetInfo.Lane laneInfo = lanes[laneIndex];

                    if (((laneInfo.m_laneType & LaneConnectionManager.LANE_TYPES) != NetInfo.LaneType.None)
                        && ((laneInfo.m_vehicleType & LaneConnectionManager.VEHICLE_TYPES)
                            != VehicleInfo.VehicleType.None)) {
                        if (connManager.GetLaneEndPoint(
                            segmentId,
                            startNode,
                            laneIndex,
                            laneId,
                            laneInfo,
                            out bool isSource,
                            out bool isTarget,
                            out Vector3? pos)) {
                            pos = pos.Value + offset;

                            float terrainY =
                                Singleton<TerrainManager>.instance.SampleDetailHeightSmooth(pos.Value);

                            var terrainPos = new Vector3(pos.Value.x, terrainY, pos.Value.z);

                            Color32 nodeMarkerColor
                                = isSource
                                      ? COLOR_CHOICES[nodeMarkerColorIndex % COLOR_CHOICES.Length]
                                      : default; // or black (not used while rendering)

                            NetLane lane = NetManager.instance.m_lanes.m_buffer[laneId];
                            Bezier3 bezier = lane.m_bezier;
                            if (startNode) {
                                bezier.a = (Vector3)pos;
                            } else {
                                bezier.d = (Vector3)pos;
                            }
                            SegmentLaneMarker segmentMarker = new SegmentLaneMarker(bezier);
                            NodeLaneMarker nodeMarker = new NodeLaneMarker {
                                TerrainPosition = terrainPos,
                                Position = (Vector3)pos,
                            };

                            bool isFoward = (laneInfo.m_direction & NetInfo.Direction.Forward) != 0;
                            int innerSimilarLaneIndex;
                            if (isFoward) {
                                innerSimilarLaneIndex = laneInfo.m_similarLaneIndex;
                            } else {
                                innerSimilarLaneIndex = laneInfo.m_similarLaneCount -
                                              laneInfo.m_similarLaneIndex - 1;
                            }
                            int outerSimilarLaneIndex = laneInfo.m_similarLaneCount - innerSimilarLaneIndex - 1;

                            laneEnds.Add(
                                new LaneEnd {
                                    SegmentId = segmentId,
                                    LaneId = laneId,
                                    NodeId = nodeId,
                                    StartNode = startNode,
                                    Color = nodeMarkerColor,
                                    IsSource = isSource,
                                    IsTarget = isTarget,
                                    LaneType = laneInfo.m_laneType,
                                    VehicleType = laneInfo.m_vehicleType,
                                    InnerSimilarLaneIndex = innerSimilarLaneIndex,
                                    OuterSimilarLaneIndex = outerSimilarLaneIndex,
                                    SegmentIndex = i,
                                    NodeMarker = nodeMarker,
                                    SegmentMarker = segmentMarker,
                                });

                            if (isSource) {
                                nodeMarkerColorIndex++;
                            }
                        }
                    }

                    laneId = NetManager.instance.m_lanes.m_buffer[laneId].m_nextLane;
                }
            }

            if (laneEnds.Count == 0) {
                return null;
            }

            foreach (LaneEnd laneEnd1 in laneEnds) {
                if (!laneEnd1.IsSource) {
                    continue;
                }

                uint[] connections =
                    LaneConnectionManager.Instance.GetLaneConnections(
                        laneEnd1.LaneId,
                        laneEnd1.StartNode);

                if ((connections == null) || (connections.Length == 0)) {
                    continue;
                }

                foreach (LaneEnd laneEnd2 in laneEnds) {
                    if (!laneEnd2.IsTarget) {
                        continue;
                    }

                    if (connections.Contains(laneEnd2.LaneId)) {
                        laneEnd1.ConnectedLaneEnds.Add(laneEnd2);
                    }
                }
            }

            return laneEnds;
        }

        private static bool CanConnect(LaneEnd source, LaneEnd target) {
            bool ret = source != target && source.IsSource && target.IsTarget;
            ret &= (target.VehicleType & source.VehicleType) != 0;

            bool IsRoad(LaneEnd laneEnd) =>
                (laneEnd.LaneType & LaneArrowManager.LANE_TYPES) != 0 &&
                (laneEnd.VehicleType & LaneArrowManager.VEHICLE_TYPES) != 0;

            // turning angle does not apply to roads.
            bool isRoad = IsRoad(source) && IsRoad(target);

            // check track turning angles are within bounds
            ret &= isRoad || CheckSegmentsTurningAngle(
                    source.SegmentId,
                    ref GetSeg(source.SegmentId),
                    source.StartNode,
                    target.SegmentId,
                    ref GetSeg(target.SegmentId),
                    target.StartNode);

            return ret;
        }

        /// <summary>
        /// Checks if the turning angle between two segments at the given node is within bounds.
        /// </summary>
        /// <param name="sourceSegmentId"></param>
        /// <param name="sourceSegment"></param>
        /// <param name="sourceStartNode"></param>
        /// <param name="targetSegmentId"></param>
        /// <param name="targetSegment"></param>
        /// <param name="targetStartNode"></param>
        /// <returns></returns>
        private static bool CheckSegmentsTurningAngle(ushort sourceSegmentId,
                                               ref NetSegment sourceSegment,
                                               bool sourceStartNode,
                                               ushort targetSegmentId,
                                               ref NetSegment targetSegment,
                                               bool targetStartNode) {
            NetManager netManager = Singleton<NetManager>.instance;
            NetInfo sourceSegmentInfo = netManager.m_segments.m_buffer[sourceSegmentId].Info;
            NetInfo targetSegmentInfo = netManager.m_segments.m_buffer[targetSegmentId].Info;

            float turningAngle = 0.01f - Mathf.Min(
                                     sourceSegmentInfo.m_maxTurnAngleCos,
                                     targetSegmentInfo.m_maxTurnAngleCos);

            if (turningAngle < 1f) {
                Vector3 sourceDirection = sourceStartNode
                                              ? sourceSegment.m_startDirection
                                              : sourceSegment.m_endDirection;
                Vector3 targetDirection;

                targetDirection = targetStartNode
                                      ? targetSegment.m_startDirection
                                      : targetSegment.m_endDirection;

                float dirDotProd = (sourceDirection.x * targetDirection.x) +
                                   (sourceDirection.z * targetDirection.z);
                return dirDotProd < turningAngle;
            }

            return true;
        }

        /// <summary>
        /// Draw a bezier curve from `start` to `end` and bent towards `middlePoint` with `color`
        /// </summary>
        /// <param name="cameraInfo">The camera to use</param>
        /// <param name="start">Where the bezier to begin</param>
        /// <param name="end">Where the bezier to end</param>
        /// <param name="middlePoint">Where the bezier is bent towards</param>
        /// <param name="color">The inner curve color</param>
        /// <param name="outlineColor">The outline color</param>
        /// <param name="size">The thickness</param>
        private void DrawLaneCurve(RenderManager.CameraInfo cameraInfo,
                                   Vector3 start,
                                   Vector3 end,
                                   Vector3 middlePoint,
                                   Color color,
                                   Color outlineColor,
                                   float size = 0.08f) {
            Bezier3 bezier;
            bezier.a = start;
            bezier.d = end;

            NetSegment.CalculateMiddlePoints(
                bezier.a,
                (middlePoint - bezier.a).normalized,
                bezier.d,
                (middlePoint - bezier.d).normalized,
                false,
                false,
                out bezier.b,
                out bezier.c);

            // Draw black outline
            RenderManager.instance.OverlayEffect.DrawBezier(
                cameraInfo,
                outlineColor,
                bezier,
                size * 1.5f,
                0,
                0,
                -1f,
                1280f,
                false,
                false);
            // Inside the outline draw colored bezier
            RenderManager.instance.OverlayEffect.DrawBezier(
                cameraInfo,
                color,
                bezier,
                size,
                0,
                0,
                -1f,
                1280f,
                false,
                true);
        }

        /// <summary>
        /// Generated with http://phrogz.net/css/distinct-colors.html
        /// HSV Value start 84%, end 37% (cutting away too bright and too dark).
        /// The colors are slightly reordered to create some variety
        /// </summary>
        private static readonly Color32[] COLOR_CHOICES
            = {
                  new Color32(240, 30, 30, 255),
                  new Color32(80, 214, 0, 255),
                  new Color32(30, 30, 214, 255),
                  new Color32(214, 136, 107, 255),
                  new Color32(120, 189, 94, 255),
                  new Color32(106, 41, 163, 255),
                  new Color32(54, 118, 214, 255),
                  new Color32(163, 57, 41, 255),
                  new Color32(54, 161, 214, 255),
                  new Color32(107, 214, 193, 255),
                  new Color32(214, 161, 175, 255),
                  new Color32(214, 0, 171, 255),
                  new Color32(151, 178, 201, 255),
                  new Color32(189, 101, 0, 255),
                  new Color32(154, 142, 189, 255),
                  new Color32(189, 186, 142, 255),
                  new Color32(176, 88, 147, 255),
                  new Color32(163, 41, 73, 255),
                  new Color32(150, 140, 0, 255),
                  new Color32(0, 140, 150, 255),
                  new Color32(0, 0, 138, 255),
                  new Color32(0, 60, 112, 255),
                  new Color32(112, 86, 56, 255),
                  new Color32(88, 112, 84, 255),
                  new Color32(0, 99, 53, 255),
                  new Color32(75, 75, 99, 255),
                  new Color32(99, 75, 85, 255)
            };
    }
}
