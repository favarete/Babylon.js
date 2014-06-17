﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Max;
using BabylonExport.Entities;
using MaxSharp;

namespace Max2Babylon
{
    partial class BabylonExporter
    {
        private void ExportMesh(Node meshNode, BabylonScene babylonScene, CancellationToken token)
        {
            if (meshNode._Node.GetBoolProperty("babylonjs_noexport"))
            {
                return;
            }

            var babylonMesh = new BabylonMesh();
            int vx1, vx2, vx3;
           
            babylonMesh.name = meshNode.Name;
            babylonMesh.id = meshNode.GetGuid().ToString();
            if (meshNode.HasParent())
            {
                babylonMesh.parentId = meshNode.Parent.GetGuid().ToString();
            }

            // Misc.
            babylonMesh.isVisible = meshNode._Node.Renderable == 1;
            babylonMesh.pickable = meshNode._Node.GetBoolProperty("babylonjs_checkpickable");
            babylonMesh.receiveShadows = meshNode._Node.RcvShadows == 1;
            babylonMesh.showBoundingBox = meshNode._Node.GetBoolProperty("babylonjs_showboundingbox");
            babylonMesh.showSubMeshesBoundingBox = meshNode._Node.GetBoolProperty("babylonjs_showsubmeshesboundingbox");

            // Collisions
            babylonMesh.checkCollisions = meshNode._Node.GetBoolProperty("babylonjs_checkcollisions");

            // Position / rotation / scaling
            var wm = meshNode.GetWorldMatrix(0, meshNode.HasParent());
            babylonMesh.position = wm.Trans.ToArraySwitched();

            var parts = Loader.Global.AffineParts.Create();
            Loader.Global.DecompAffine(wm, parts);

            //var rotate = new float[3];

            //IntPtr xPtr = Marshal.AllocHGlobal(sizeof(float));
            //IntPtr yPtr = Marshal.AllocHGlobal(sizeof(float));
            //IntPtr zPtr = Marshal.AllocHGlobal(sizeof(float));
            //parts.Q.GetEuler(xPtr, yPtr, zPtr);

            //Marshal.Copy(xPtr, rotate, 0, 1);
            //Marshal.Copy(yPtr, rotate, 1, 1);
            //Marshal.Copy(zPtr, rotate, 2, 1);

            //var temp = -rotate[1];
            //rotate[0] = rotate[0] * parts.F;
            //rotate[1] = -rotate[2] * parts.F;
            //rotate[2] = temp * parts.F;

            //babylonMesh.rotation = rotate;

            babylonMesh.rotationQuaternion = parts.Q.ToArray();

            babylonMesh.scaling = parts.K.ToArraySwitched();

            if (wm.Parity)
            {
                vx1 = 2;
                vx2 = 1;
                vx3 = 0;
            }
            else
            {
                vx1 = 0;
                vx2 = 1;
                vx3 = 2;
            }

            // Pivot
            var pivotMatrix = Matrix3.Identity._IMatrix3;
            pivotMatrix.PreTranslate(meshNode._Node.ObjOffsetPos);
            Loader.Global.PreRotateMatrix(pivotMatrix, meshNode._Node.ObjOffsetRot);
            Loader.Global.ApplyScaling(pivotMatrix, meshNode._Node.ObjOffsetScale);
            babylonMesh.pivotMatrix = pivotMatrix.ToArray();

            // Mesh
            var objectState = meshNode._Node.EvalWorldState(0, false);
            bool mustBeDeleted;
            var triObject = objectState.Obj.GetMesh(out mustBeDeleted);
            var mesh = triObject != null ? triObject.Mesh : null;
            var computedMesh = meshNode.GetMesh();

            RaiseMessage(meshNode.Name, mesh == null ? System.Drawing.Color.Gray : System.Drawing.Color.Black, true);

            if (mesh != null)
            {
                mesh.BuildNormals();

                if (mesh.NumFaces < 1)
                {
                    RaiseError(string.Format("Mesh {0} has no face", babylonMesh.name));
                }

                if (mesh.NumVerts < 3)
                {
                    RaiseError(string.Format("Mesh {0} has not enough vertices", babylonMesh.name));
                }

                if (mesh.NumVerts >= 65536)
                {
                    RaiseError(string.Format("Mesh {0} has too many vertices (more than 65535)", babylonMesh.name));
                }

                // Material
                var mtl = meshNode.Material;
                var multiMatsCount = 1;

                if (mtl != null)
                {
                    babylonMesh.materialId = mtl.GetGuid().ToString();

                    if (!referencedMaterials.Contains(mtl))
                    {
                        referencedMaterials.Add(mtl);
                    }

                    multiMatsCount = Math.Max(mtl.NumSubMaterials, 1);
                }

                babylonMesh.visibility = meshNode._Node.GetVisibility(0, Interval.Forever._IInterval);

                var vertices = new List<GlobalVertex>();
                var indices = new List<int>();
                var matIDs = new List<int>();

                var hasUV = mesh.NumTVerts > 0;
                var hasUV2 = mesh.GetNumMapVerts(2) > 0;

                var optimizeVertices = meshNode._Node.GetBoolProperty("babylonjs_optimizevertices");

                // Compute normals
                VNormal[] vnorms = null;
                List<GlobalVertex>[] verticesAlreadyExported = null;

                if (!optimizeVertices)
                {
                    vnorms = Tools.ComputeNormals(mesh);
                }
                else
                {
                    verticesAlreadyExported = new List<GlobalVertex>[mesh.NumVerts];
                }

                for (var face = 0; face < mesh.NumFaces; face++)
                {
                    indices.Add(CreateGlobalVertex(mesh, computedMesh, face, vx1, vertices, hasUV, hasUV2, vnorms, verticesAlreadyExported));
                    indices.Add(CreateGlobalVertex(mesh, computedMesh, face, vx2, vertices, hasUV, hasUV2, vnorms, verticesAlreadyExported));
                    indices.Add(CreateGlobalVertex(mesh, computedMesh, face, vx3, vertices, hasUV, hasUV2, vnorms, verticesAlreadyExported));
                    matIDs.Add(mesh.Faces[face].MatID % multiMatsCount);
                    if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                }

                if (vertices.Count >= 65536)
                {
                    RaiseError(string.Format("Mesh {0} has too many vertices: {1} (limit is 65535)", babylonMesh.name, vertices.Count));

                    if (!optimizeVertices)
                    {
                        RaiseError("You can try to optimize your object using [Try to optimize vertices] option");
                    }
                }

                RaiseMessage(string.Format("{0} vertices, {1} faces", vertices.Count, indices.Count / 3), true, false, true);

                // Buffers
                babylonMesh.positions = vertices.SelectMany(v => v.Position.ToArraySwitched()).ToArray();
                babylonMesh.normals = vertices.SelectMany(v => v.Normal.ToArraySwitched()).ToArray();
                if (hasUV)
                {
                    babylonMesh.uvs = vertices.SelectMany(v => v.UV.ToArray()).ToArray();
                }
                if (hasUV2)
                {
                    babylonMesh.uvs2 = vertices.SelectMany(v => v.UV2.ToArray()).ToArray();
                }

                // Submeshes
                var sortedIndices = new List<int>();
                var subMeshes = new List<BabylonSubMesh>();
                var indexStart = 0;
                for (var index = 0; index < multiMatsCount; index++)
                {
                    var subMesh = new BabylonSubMesh();
                    var indexCount = 0;
                    var minVertexIndex = int.MaxValue;
                    var maxVertexIndex = int.MinValue;

                    subMesh.indexStart = indexStart;
                    subMesh.materialIndex = index;

                    for (var face = 0; face < matIDs.Count; face++)
                    {
                        if (matIDs[face] == index)
                        {
                            var a = indices[3 * face];
                            var b = indices[3 * face + 1];
                            var c = indices[3 * face + 2];

                            sortedIndices.Add(a);
                            sortedIndices.Add(b);
                            sortedIndices.Add(c);
                            indexCount += 3;

                            if (a < minVertexIndex)
                            {
                                minVertexIndex = a;
                            }

                            if (b < minVertexIndex)
                            {
                                minVertexIndex = b;
                            }

                            if (c < minVertexIndex)
                            {
                                minVertexIndex = c;
                            }

                            if (a > maxVertexIndex)
                            {
                                maxVertexIndex = a;
                            }

                            if (b > maxVertexIndex)
                            {
                                maxVertexIndex = b;
                            }

                            if (c > maxVertexIndex)
                            {
                                maxVertexIndex = c;
                            }
                        }
                    }
                    if (indexCount != 0)
                    {

                        subMesh.indexCount = indexCount;
                        subMesh.verticesStart = minVertexIndex;
                        subMesh.verticesCount = maxVertexIndex - minVertexIndex + 1;

                        indexStart += indexCount;

                        subMeshes.Add(subMesh);
                    }
                    if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                }
                babylonMesh.subMeshes = subMeshes.ToArray();


                // Buffers - Indices
                babylonMesh.indices = sortedIndices.ToArray();

                if (mustBeDeleted)
                {
                    triObject.DeleteMe();
                }
            }


            // Animations
            var animations = new List<BabylonAnimation>();

            if (!ExportVector3Controller(meshNode._Node.TMController.PositionController, "position", animations))
            {
                ExportVector3Animation("position", animations, key =>
                {
                    var worldMatrix = meshNode.GetWorldMatrix(key, meshNode.HasParent());
                    return worldMatrix.Trans.ToArraySwitched();
                });
            }

            if (!ExportQuaternionController(meshNode._Node.TMController.RotationController, "rotationQuaternion", animations))
            {
                ExportQuaternionAnimation("rotationQuaternion", animations, key =>
                {
                    var worldMatrix = meshNode.GetWorldMatrix(key, meshNode.HasParent());

                    var affineParts = Loader.Global.AffineParts.Create();
                    Loader.Global.DecompAffine(worldMatrix, affineParts);

                    return affineParts.Q.ToArray();
                });
            }

            if (!ExportVector3Controller(meshNode._Node.TMController.ScaleController, "scaling", animations))
            {
                ExportVector3Animation("scaling", animations, key =>
                {
                    var worldMatrix = meshNode.GetWorldMatrix(key, meshNode.HasParent());

                    var affineParts = Loader.Global.AffineParts.Create();
                    Loader.Global.DecompAffine(worldMatrix, affineParts);

                    return affineParts.K.ToArraySwitched();
                });
            }

            if (!ExportFloatController(meshNode._Node.VisController, "visibility", animations))
            {
                ExportFloatAnimation("visibility", animations, key => new[] {meshNode._Node.GetVisibility(key, Interval.Forever._IInterval)});
            }

            babylonMesh.animations = animations.ToArray();

            if (meshNode._Node.GetBoolProperty("babylonjs_autoanimate"))
            {
                babylonMesh.autoAnimate = true;
                babylonMesh.autoAnimateFrom = (int)meshNode._Node.GetFloatProperty("babylonjs_autoanimate_from");
                babylonMesh.autoAnimateTo = (int)meshNode._Node.GetFloatProperty("babylonjs_autoanimate_to");
                babylonMesh.autoAnimateLoop = meshNode._Node.GetBoolProperty("babylonjs_autoanimateloop");
            }

            babylonScene.MeshesList.Add(babylonMesh);
        }

        int CreateGlobalVertex(IMesh mesh, Mesh computedMesh, int face, int facePart, List<GlobalVertex> vertices, bool hasUV, bool hasUV2, VNormal[] vnorms, List<GlobalVertex>[] verticesAlreadyExported)
        {
            var faceObject = mesh.Faces[face];
            var vertexIndex = (int)faceObject.V[facePart];

            var vertex = new GlobalVertex
            {
                BaseIndex = vertexIndex,
                Position = mesh.Verts[vertexIndex],
                Normal = (vnorms != null) ? vnorms[vertexIndex].GetNormal(faceObject.SmGroup) : computedMesh.vnormals[vertexIndex]._IPoint3
            };

            if (hasUV)
            {
                var tvertexIndex = (int)mesh.TvFace[face].T[facePart];
                vertex.UV = Loader.Global.Point2.Create(mesh.TVerts[tvertexIndex].X, mesh.TVerts[tvertexIndex].Y);
            }

            if (hasUV2)
            {
                var tvertexIndex = (int)mesh.MapFaces(2)[face].T[facePart];
                vertex.UV2 = Loader.Global.Point2.Create(mesh.MapVerts(2)[tvertexIndex].X, mesh.MapVerts(2)[tvertexIndex].Y);
            }

            if (verticesAlreadyExported != null)
            {
                if (verticesAlreadyExported[vertexIndex] != null)
                {
                    var index = verticesAlreadyExported[vertexIndex].IndexOf(vertex);

                    if (index > -1)
                    {
                        return verticesAlreadyExported[vertexIndex][index].CurrentIndex;
                    }
                }
                else
                {
                    verticesAlreadyExported[vertexIndex] = new List<GlobalVertex>();
                }

                vertex.CurrentIndex = vertices.Count;
                verticesAlreadyExported[vertexIndex].Add(vertex);
            }

            vertices.Add(vertex);

            return vertices.Count - 1;
        }
    }
}