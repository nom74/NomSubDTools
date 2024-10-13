using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using System;


namespace NomSubDTools.Commands
{
    public class AlignVerticesXy : Command
    {
        public AlignVerticesXy()
        {
            // Rhino only creates one instance of each command class defined in a
            // plug-in, so it is safe to store a refence in a static property.
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static AlignVerticesXy Instance { get; private set; }

        ///<returns>The command name as it appears on the Rhino command line.</returns>
        public override string EnglishName => "nomSubDAlignXY";


        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Step 1: Retrieve all visible SubD objects
            var subdObjects = doc.Objects.FindByObjectType(ObjectType.SubD);

            if (subdObjects == null || subdObjects.Length == 0)
            {
                RhinoApp.WriteLine("No visible SubD objects found.");
                return Result.Failure;
            }

            // Step 1: Create options for alignment using Line or Curve
            GetOption getOption = new GetOption();
            getOption.SetCommandPrompt("Align to a Line or Curve? (Line is default, l for Line, c for Curve)");

            // Add options for Line and Curve with keyboard shortcuts
            getOption.AcceptNothing(true); // If Enter is pressed without selection, default option is used
            getOption.AddOption("Line", "l");
            getOption.AddOption("Curve", "c");

            // Get user's choice
            GetResult getResult = getOption.Get();
            Boolean bAlignToLine = true; // Default is Line

            if (getResult == GetResult.Option)
            {
                // Check if user pressed "l" or "c"
                if (getOption.Option().EnglishName == "Curve")
                {
                    bAlignToLine = false; // Curve chosen
                    RhinoApp.WriteLine("Aligning to Curve.");
                }
                else if (getOption.Option().EnglishName == "Line")
                {
                    RhinoApp.WriteLine("Aligning to Line.");
                }
            }
            else if (getResult == GetResult.Nothing)
            {
                // If Enter is pressed without selection, default option Line is used
                RhinoApp.WriteLine("Aligning to Line (default).");
            }

            // Step 2: Select multiple control points
            GetObject getObject = new GetObject();
            getObject.SetCommandPrompt("Select control points on SubD object (you can select multiple points)");
            getObject.GeometryFilter = ObjectType.AnyObject; // Allow selection of any geometry
            getObject.SubObjectSelect = true; // Allow selection of sub-objects
            getObject.GetMultiple(2, 0); // Settings for multiple object selection

            if (getObject.CommandResult() != Result.Success)
                return getObject.CommandResult();

            Line line = new Line();
            Curve curve = null;

            if (bAlignToLine)
            {
                // Step 3: Select start and end points for the line with dynamic preview
                Point3d p3dStartPoint, p3dEndPoint;
                Result rc = RhinoGet.GetPoint("Select start point of the line", false, out p3dStartPoint);
                if (rc != Result.Success)
                    return rc;

                // Create dynamic point picker for the end point
                GetPoint getEndPoint = new GetPoint();
                getEndPoint.SetCommandPrompt("Select end point of the line");
                getEndPoint.DynamicDraw += (_, e) =>
                {
                    // Draw the red line from start to current mouse point
                    e.Display.DrawLine(p3dStartPoint, e.CurrentPoint, System.Drawing.Color.Red);
                };
                getEndPoint.Get();
                if (getEndPoint.CommandResult() != Result.Success)
                {
                    return getEndPoint.CommandResult();
                }

                p3dEndPoint = getEndPoint.Point();

                Point3d p3dStartPointXy = new Point3d(p3dStartPoint.X, p3dStartPoint.Y, 0.00);
                Point3d p3dEndPointXy = new Point3d(p3dEndPoint.X, p3dEndPoint.Y, 0.00);

                line = new Line(p3dStartPointXy, p3dEndPointXy);

            }
            else
            {
                // Step 3: Select a curve for alignment
                ObjRef objRef;
                var rc = RhinoGet.GetOneObject("Select alignment curve", false, ObjectType.Curve, out objRef);
                if (rc != Result.Success)
                    return rc;

                curve = objRef.Curve();
                if (curve == null)
                {
                    RhinoApp.WriteLine("Selected object is not a valid curve.");
                    return Result.Failure;
                }
            }

            // Step 4: Iterate over selected points and check their SubD objects
            foreach (var objRef in getObject.Objects())
            {
                // Get Object ID and find corresponding SubD object
                Guid guIdSubd = objRef.ObjectId; // Object ID of the selected SubD object
                int vertexIndex = objRef.GeometryComponentIndex.Index;

                SubD subDSelected = null;
                SubDVertex subDVertexSelected = null;

                foreach (var subdObject in subdObjects)
                {
                    if (subdObject.Id != guIdSubd) continue; // Check if Object ID matches

                    SubD subd = subdObject.Geometry as SubD;
                    if (subd != null)
                    {


                        // Find corresponding vertex
                        SubDVertex subDVertex = subd.Vertices.Find(vertexIndex);
                        if (subDVertex != null)
                        {
                            subDSelected = subd;
                            subDVertexSelected = subDVertex;
                            break;
                        }
                    }
                }

                if (subDSelected == null)
                {
                    RhinoApp.WriteLine("No SubD object contains the selected control point.");
                    continue;
                }

                Point3d pt3dVertex = subDVertexSelected.ControlNetPoint;

                // Align to curve or line
                Point3d pt3dClosest;
                if (bAlignToLine)
                {
                    // Align to line
                    double dT = line.ClosestParameter(pt3dVertex);
                    pt3dClosest = line.PointAt(dT);
                }
                else
                {
                    // Align to curve
                    double dT;
                    if (!curve.ClosestPoint(pt3dVertex, out dT))
                    {
                        RhinoApp.WriteLine("Could not find closest point on curve.");
                        continue;
                    }
                    pt3dClosest = curve.PointAt(dT);
                }

                // Create new point preserving original Z coordinate
                Point3d pt3dNewPpoint = new Point3d(pt3dClosest.X, pt3dClosest.Y, pt3dVertex.Z);

                // Move point to new position
                subDVertexSelected.SetControlNetPoint(pt3dNewPpoint, false);

            }

            // Step 5: Update all modified SubD objects
            foreach (var subdObject in subdObjects)
            {
                SubD subd = subdObject.Geometry as SubD;
                if (subd != null)
                {
                    subd.UpdateSurfaceMeshCache(true);
                    subd.ClearEvaluationCache();
                    doc.Objects.Replace(subdObject.Id, subd);
                }
            }

            // Render changes
            doc.Views.Redraw();
            return Result.Success;
        }

    }
}





