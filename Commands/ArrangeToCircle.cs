using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using NomSubDTools.MyUtils;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;


namespace NomSubDTools.Commands
{
    public class ArrangeToCircle : Command
    {
        public ArrangeToCircle()
        {
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static ArrangeToCircle Instance { get; private set; }

        public override string EnglishName => "nomSubDArrangeToCircle";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Find all visible SubD objects
            var subDObjects = doc.Objects.FindByObjectType(ObjectType.SubD);


            // Check if any SubD objects were found
            if (subDObjects == null || subDObjects.Length == 0)
            {
                RhinoApp.WriteLine("No visible SubD objects found.");
                return Result.Failure;
            }


            // --------------------------------------
            // Step 1: Select multiple control points
            GetObject getObject = new GetObject();
            getObject.SetCommandPrompt("Select  more then 4 control points on SubD object");
            getObject.GeometryFilter = ObjectType.AnyObject;
            getObject.SubObjectSelect = true;
            getObject.GetMultiple(2, 0);


            // Check if the command was successful
            if (getObject.CommandResult() != Result.Success)
            {
                return getObject.CommandResult();
            }


            // --------------------------------------
            // Step 2: Collect selected points and map them to vertices
            List<SubDVertex> lsSubDVertexSelected = new List<SubDVertex>(); // List to store selected vertices
            foreach (var objRef in getObject.Objects())
            {
                int iVertexIndex = objRef.GeometryComponentIndex.Index;


                // Iterate through found SubD objects
                foreach (var subDObject in subDObjects)
                {
                    SubD subD = subDObject.Geometry as SubD; // Cast the geometry to SubD
                    if (subD != null &&
                        subDObject.Id == objRef.ObjectId) // Check if object matches the selected reference
                    {
                        SubDVertex subDVertex = subD.Vertices.Find(iVertexIndex); // Find the vertex by index
                        if (subDVertex != null)
                        {
                            lsSubDVertexSelected.Add(subDVertex); // Add vertex to the list
                            break; // Break the inner loop if found
                        }
                    }
                }
            }


            // Check if enough points were selected to define a plane
            if (lsSubDVertexSelected.Count < 4)
            {
                RhinoApp.WriteLine("Not enough points selected to make circularized polygon.");
                return Result.Failure;
            }




            // Convert SubDVertex list to Point3d list
            List<Point3d> lsPt3DVertex = lsSubDVertexSelected.Select(v => v.ControlNetPoint).ToList();


            // Create a conduit for dynamic rendering
            MyCustomOverlayConduit myCustomOverlayConduitForSelectedVertex =
                new MyCustomOverlayConduit(lsPt3DVertex, Color.Red)
                {
                    Enabled = true
                };
            doc.Views.Redraw();


            // Calculate the centroid of all points (average point)
            Point3d pt3dCentroid = new Point3d(
                lsPt3DVertex.Average(pt => pt.X),
                lsPt3DVertex.Average(pt => pt.Y),
                lsPt3DVertex.Average(pt => pt.Z)
            );


            // Fit a plane to the points, ensuring the origin of the plane is at the centroid
            Plane planeFit;
            Plane.FitPlaneToPoints(lsPt3DVertex, out planeFit);
            planeFit.Origin = pt3dCentroid; // Set the origin of the plane to the centroid



            // --------------------------------------
            // Step 3: Sort selected vertices 
            SortAndUpdateVertices(ref lsSubDVertexSelected, planeFit,  doc, myCustomOverlayConduitForSelectedVertex); // Call the helper method to sort and update vertices



            // --------------------------------------
            // Step 4: Calculate average distance from the center
            double dAverageDistance = 0;
            foreach (var pt3d in lsSubDVertexSelected)
            {
                dAverageDistance += pt3d.ControlNetPoint.DistanceTo(planeFit.Origin); // Accumulate distances
            }

            dAverageDistance /= lsSubDVertexSelected.Count; // Calculate the average distance



            // --------------------------------------
            // Step 5: Create a base polygon in the plane
            int iSides = lsSubDVertexSelected.Count;
            Polyline plPolygon = CreateInitialPolygon(dAverageDistance, iSides, planeFit, doc);


            // Call function for interactive rotation of the polygon
            Result resultRotation = InteractivePolygonRotation(doc, plPolygon, lsSubDVertexSelected, planeFit, myCustomOverlayConduitForSelectedVertex);
            if (resultRotation != Result.Success)
            {
                myCustomOverlayConduitForSelectedVertex.Enabled = false; // Disable conduit
                doc.Views.Redraw(); // Redraw the view
                return resultRotation; // Return the result of the rotation
            }



            // --------------------------------------
            // Step 6:  Call function to map SubD vertices to the polygon
            MapSubDVerticesToPolygon(lsSubDVertexSelected, plPolygon);


            // --------------------------------------
            // Step 7: Update all modified SubD objects
            foreach (var subDObject in subDObjects)
            {
                SubD subD = subDObject.Geometry as SubD; // Cast the geometry to SubD
                if (subD != null)
                {
                    subD.UpdateSurfaceMeshCache(true); // Update the mesh cache
                    subD.ClearEvaluationCache(); // Clear the evaluation cache
                    doc.Objects.Replace(subDObject.Id, subD); // Replace the old SubD object with the modified one
                }
            }



            // Redraw the document
            myCustomOverlayConduitForSelectedVertex.Enabled = false;
            doc.Views.Redraw();
            return Result.Success;
        }



        private Result InteractivePolygonRotation(RhinoDoc doc, Polyline plPolygon, List<SubDVertex> lsSubDVertexSelected, Plane planeFit, MyCustomOverlayConduit myCustomOverlayConduitForSelectedVertex)
        {
            // If no selected points, there is no point in continuing
            if (lsSubDVertexSelected.Count == 0)
            {
                RhinoApp.WriteLine("No points selected to rotate towards.");
                return Result.Failure; // Return failure if no points are selected
            }

            // First point in the list of selected points
            //Point3d pt3dFirstSelectedPoint = lsSubDVertexSelected[0].ControlNetPoint;

            // Create a point getter for dynamic rotation
            GetPoint getPoint = new GetPoint();
            getPoint.SetCommandPrompt("Rotate the polygon dynamically. Press Enter to confirm or ESC to cancel. Move points order Forward 'F' or Back 'B'");
            getPoint.SetBasePoint(planeFit.Origin, true); // The center of the polygon will be the base point for rotation
            getPoint.DrawLineFromPoint(planeFit.Origin, true); // Show line from the center of the polygon to the cursor

            getPoint.AddOption("Forward", "F", false);
            getPoint.AddOption("Back", "B", false);


            // Dynamic drawing of the polygon while the mouse moves
            Polyline plOriginalPolygon = plPolygon.Duplicate(); // Store the original polygon before starting rotation


            // Ensure that the active view is set correctly
            Rhino.Display.RhinoView activeView = RhinoDoc.ActiveDoc.Views.ActiveView;

            // Store the original construction plane
            Plane planeOriginalCPlane = activeView.ActiveViewport.ConstructionPlane();

            // Set our plane as a temporary CPlane before starting dynamic drawing
            activeView.ActiveViewport.SetConstructionPlane(planeFit);
            activeView.Redraw(); // Ensure the plane is applied immediately

            // Now initialize dynamic drawing
            getPoint.DynamicDraw += (_, e) =>
            {
                // First point in the list of selected points
                Point3d pt3dFirstSelectedPoint = lsSubDVertexSelected[0].ControlNetPoint;

                // Drawing is now happening in our plane
                Point3d pt3dCursorPoint = e.CurrentPoint;  // Rhino now places the point directly in our plane

                // Calculate the directional vector from the center of the polygon to the cursor point (in our plane)
                Vector3d v3dDirectionToCursor = pt3dCursorPoint - planeFit.Origin;

                // Initial direction: from the center of the polygon to the first point of the polygon
                Vector3d v3dInitialDirection = plOriginalPolygon[0] - planeFit.Origin;

                // Calculate the rotation angle between the initial direction and the direction to the cursor point (in our plane)
                double dRotationAngle = Vector3d.VectorAngle(v3dInitialDirection, v3dDirectionToCursor, planeFit);

                // Define a transformation matrix for rotation around the center of the polygon in our plane
                Transform transformRotation = Transform.Rotation(dRotationAngle, planeFit.Normal, planeFit.Origin);

                // Create a rotated polygon based on the original polygon
                Polyline plRotatedPolygon = plOriginalPolygon.Duplicate(); // Duplicate the original polygon
                plRotatedPolygon.Transform(transformRotation); // Apply the rotation

                // Draw the rotated polygon
                e.Display.DrawPolyline(plRotatedPolygon, Color.Yellow, 2);

                // Close the polygon by adding a line from the last point to the first
                if (plRotatedPolygon.Count > 1)
                {
                    e.Display.DrawLine(plRotatedPolygon[plRotatedPolygon.Count - 1], plRotatedPolygon[0], Color.Yellow, 2);
                }



                // Draw a black line from the center of the polygon to the cursor point (in the polygon plane)
                e.Display.DrawLine(planeFit.Origin, pt3dCursorPoint, Color.Black, 2);

                // Draw a red line from the center of the polygon to the first point of the polygon after rotation
                Point3d pt3dFirstRotatedPoint = plRotatedPolygon[0];
                e.Display.DrawLine(planeFit.Origin, pt3dFirstRotatedPoint, Color.Red, 2);

                // Draw a line between the first point of the rotated polygon and the first selected point with an arrow
                e.Display.DrawLine(pt3dFirstSelectedPoint, pt3dFirstRotatedPoint, Color.Black, 2);
                e.Display.DrawArrow(new Line(pt3dFirstSelectedPoint, pt3dFirstRotatedPoint), Color.Black);

                // Label the corresponding point on the polygon after rotation
                for (int i = 0; i < lsSubDVertexSelected.Count; i++)
                {
                    if (i < plRotatedPolygon.Count)
                    {
                        e.Display.Draw2dText(i.ToString(), Color.Blue, plRotatedPolygon[i], false, 22);
                    }
                }
            };


            while (true)
            {
                // Get a point from the user
                GetResult res = getPoint.Get();

                if (res == GetResult.Option)
                {
                    // Detect selected options
                    CommandLineOption option = getPoint.Option();
                    switch (option.EnglishName)
                    {
                        case "Forward":
                            RhinoApp.WriteLine("Stepping vertices...");
                            // Store the first element
                            SubDVertex subDVertexFirstForward = lsSubDVertexSelected[0];
                            lsSubDVertexSelected.RemoveAt(0); // Remove the first element
                            lsSubDVertexSelected.Add(subDVertexFirstForward); // Add it to the end

                            // Update and redraw
                            List<Point3d> lsPt3DVerteFx = lsSubDVertexSelected.Select(v => v.ControlNetPoint).ToList();
                            myCustomOverlayConduitForSelectedVertex.UpdateVertices(lsPt3DVerteFx);
                            doc.Views.Redraw(); // Redraw the view
                            break;

                        case "Back":
                            RhinoApp.WriteLine("Stepping vertices...");
                            SubDVertex subDVertexLast =
                                lsSubDVertexSelected[lsSubDVertexSelected.Count - 1]; // Store the last element
                            lsSubDVertexSelected.RemoveAt(lsSubDVertexSelected.Count - 1); // Remove the last element
                            lsSubDVertexSelected.Insert(0, subDVertexLast); // Insert at the beginning

                            // Update and redraw
                            List<Point3d> lsPt3DVertexB = lsSubDVertexSelected.Select(v => v.ControlNetPoint).ToList();
                            myCustomOverlayConduitForSelectedVertex.UpdateVertices(lsPt3DVertexB);
                            doc.Views.Redraw(); // Redraw the view
                            break;
                    }

                    continue;
                }

                if (res == GetResult.Point || res == GetResult.Nothing)
                {
                    // Get the final point to confirm the rotation
                    Point3d pt3dRotationPoint = getPoint.Point();
                    Vector3d v3dDirection = pt3dRotationPoint - planeFit.Origin;
                    v3dDirection.Transform(Transform.PlanarProjection(planeFit)); // Project onto the fitted plane

                    Vector3d v3dInitialDirection = plPolygon[0] - planeFit.Origin;
                    v3dInitialDirection.Transform(Transform.PlanarProjection(planeFit)); // Project onto the fitted plane

                    double dRotationAngle = Vector3d.VectorAngle(v3dInitialDirection, v3dDirection, planeFit); // Calculate rotation angle

                    // Apply the final rotation to the polygon in the projected plane
                    Transform transformRotation = Transform.Rotation(dRotationAngle, planeFit.Normal, planeFit.Origin);
                    plPolygon.Transform(transformRotation); // Transform the original polygon

                    doc.Views.Redraw(); // Redraw the view
                    break;
                }
                else if (res == GetResult.Cancel)
                {
                    // After completion, restore the original CPlane
                    activeView.ActiveViewport.SetConstructionPlane(planeOriginalCPlane);
                    activeView.Redraw(); // Restore the original plane

                    // Cancel the action 
                    doc.Views.Redraw(); // Redraw the view
                    return Result.Cancel; // Return cancel result
                }
            }

            // After completion, restore the original CPlane
            activeView.ActiveViewport.SetConstructionPlane(planeOriginalCPlane);
            activeView.Redraw(); // Restore the original plane

            doc.Views.Redraw(); // Redraw the view
            return Result.Success; // Indicate success
        }




        // Method to sort vertices and update conduit
        private void SortAndUpdateVertices(ref  List<SubDVertex> lsSubDVertexSelected, Plane planeFit, RhinoDoc doc, MyCustomOverlayConduit myCustomOverlayConduit)
        {
            lsSubDVertexSelected = SortVerticesByAngle(lsSubDVertexSelected, planeFit, doc); // Sort vertices by angle

            // Update conduit with newly sorted vertices
            List<Point3d> lsPt3DVertex = lsSubDVertexSelected.Select(v => v.ControlNetPoint).ToList();
            myCustomOverlayConduit.UpdateVertices(lsPt3DVertex); // Update the conduit
            doc.Views.Redraw(); // Redraw the view
        }



        private Polyline CreateInitialPolygon( double dRadius, int iSides, Plane plane, RhinoDoc doc)
        {
            Polyline plPolygon = new Polyline(); // Initialize a new Polyline
            double dAngleStep = 2 * Math.PI / iSides; // Calculate the angle step based on the number of sides

            // Loop through the number of sides to create each vertex of the polygon
            for (int i = 0; i < iSides; i++)
            {
                double dAngle = i * dAngleStep; // Calculate the angle for the current vertex
                double dX = dRadius * Math.Cos(dAngle); // Calculate the X coordinate
                double dY = dRadius * Math.Sin(dAngle); // Calculate the Y coordinate

                Point3d pt3dPointInXyPlane = new Point3d(dX, dY, 0); // Create a point in the XY plane
                Point3d pt3dTransformedPoint = plane.PointAt(pt3dPointInXyPlane.X, pt3dPointInXyPlane.Y); // Transform the point into the defined plane

                // Move the points so they are properly positioned around the center
                //pt3dTransformedPoint += pt3dCenter - plane.Origin; // Adjust the position based on the center of the polygon
                plPolygon.Add(pt3dTransformedPoint); // Add the transformed point to the polygon
            }

            plPolygon = SortAndCreatePolygon(plPolygon, plane, doc);
            
            return plPolygon; // Return the created polygon
        }



        private void MapSubDVerticesToPolygon(List<SubDVertex> lsSubDVertices, Polyline plPolygon)
        {
            // Check if the lists have the same length
            if (lsSubDVertices.Count != plPolygon.Count)
            {
                throw new ArgumentException("The number of SubD vertices and polygon vertices must match."); // Throw an exception if they don't match
            }

            // Assign each SubD vertex to the corresponding polygon vertex
            for (int i = 0; i < lsSubDVertices.Count; i++)
            {
                Point3d pt3dPolygonVertex = plPolygon[i]; // Get the polygon vertex at position i
                lsSubDVertices[i].SetControlNetPoint(pt3dPolygonVertex, false); // Set the SubD vertex to the corresponding polygon vertex
            }
        }




        private Polyline SortAndCreatePolygon(Polyline plPolygon, Plane planeFit, RhinoDoc doc)
        {
            // Choose the first point as the reference point
            Point3d referencePoint = planeFit.ClosestPoint(plPolygon[0]);

            // Calculate the base vector from the center of the plane to the reference point
            Vector3d baseVector = referencePoint - planeFit.Origin;

            // Create a list to store vertices and their angles
            List<Tuple<Point3d, double>> verticesWithAngles = new List<Tuple<Point3d, double>>();

            // Loop through each vertex of the polygon
            for (int i = 0; i < plPolygon.Count; i++)
            {
                if (i == 0)
                {
                    // The first point is the reference point and has an angle of 0
                    verticesWithAngles.Add(Tuple.Create(plPolygon[i], 0.0));
                }
                else
                {
                    // Project the vertex onto the plane
                    Point3d projectedPoint = planeFit.ClosestPoint(plPolygon[i]);

                    // Calculate the vector from the center of the plane to the current point
                    Vector3d currentVector = projectedPoint - planeFit.Origin;

                    // Calculate the angle between the base vector and the current vector using the dot product
                    double angle = Vector3d.VectorAngle(baseVector, currentVector, planeFit);

                    // Invert the angle to sort clockwise instead of counterclockwise
                    angle = 2 * Math.PI - angle;

                    // Store the vertex and its calculated angle
                    verticesWithAngles.Add(Tuple.Create(plPolygon[i], angle));
                }
            }

            // Redraw the view to visualize the lines
            doc.Views.Redraw();

            // Sort the vertices by their angles in ascending order (now clockwise)
            verticesWithAngles.Sort((a, b) => a.Item2.CompareTo(b.Item2));

            // Create a new polygon with sorted vertices
            Polyline sortedPolygon = new Polyline();
            foreach (var vertexWithAngle in verticesWithAngles)
            {
                sortedPolygon.Add(vertexWithAngle.Item1);
            }

            return sortedPolygon; // Return the sorted polygon
        }



        private List<SubDVertex> SortVerticesByAngle(List<SubDVertex> lsSubDVertices, Plane planeFit, RhinoDoc doc)

        {
            // Project points onto the fitted plane
            List<Point3d> lsProjectedPoints = new List<Point3d>();
            foreach (var subDVertex in lsSubDVertices)
            {
                lsProjectedPoints.Add(planeFit.ClosestPoint(subDVertex.ControlNetPoint));
            }

            // Choose the first point as the reference point
            Point3d pt3dReferencePoint = lsProjectedPoints[0];

            // Calculate the base vector from the center of the plane to the reference point
            Vector3d v3dBaseVector = pt3dReferencePoint - planeFit.Origin;

            // Create a list to store vertices and their angles
            List<Tuple<SubDVertex, double>> lsVerticesWithAngles = new List<Tuple<SubDVertex, double>>();

            // Draw a line from the center of the plane to each point for visualization
            foreach (var i in Enumerable.Range(0, lsProjectedPoints.Count))
            {
                if (i == 0)
                {
                    // The first point is the reference point and has an angle of 0
                    lsVerticesWithAngles.Add(Tuple.Create(lsSubDVertices[i], 0.0));
                }
                else
                {
                    // Calculate the vector from the center of the plane to the current point
                    Vector3d v3dCurrentVector = lsProjectedPoints[i] - planeFit.Origin;

                    // Calculate the angle between the base vector and the current vector using the dot product
                    double dAngle = Vector3d.VectorAngle(v3dBaseVector, v3dCurrentVector, planeFit);

                    // Invert the angle to sort clockwise instead of counterclockwise
                    dAngle = 2 * Math.PI - dAngle;

                    // Store the vertex and its calculated angle
                    lsVerticesWithAngles.Add(Tuple.Create(lsSubDVertices[i], dAngle));
                }
            }

            // Redraw the view to visualize the lines
            doc.Views.Redraw();

            // Sort the vertices by their angles in ascending order (now clockwise)
            lsVerticesWithAngles.Sort((a, b) => a.Item2.CompareTo(b.Item2));

            // Return the sorted vertices
            return lsVerticesWithAngles.Select(tuple => tuple.Item1).ToList();
        }

    }
}