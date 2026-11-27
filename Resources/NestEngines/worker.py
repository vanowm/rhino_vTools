"""Local vNest worker: one JSON job on stdin, candidate layouts on stdout."""

import json
import math
import sys
import threading
import time


# Poll the native Sparrow progress queue ten times per second.
PROGRESS_POLL_SECONDS = 0.1


def send(**message):
    print(json.dumps(message, allow_nan=False, separators=(",", ":")), flush=True)


def polygon_area(points):
    return abs(math.fsum(x * ty - tx * y for (x, y), (tx, ty) in
                         zip(points, points[1:] + points[:1]))) / 2


def packing_solver(job):
    import packingsolver.irregular as solver

    def run(objective, bin_count, seconds):
        builder = solver.InstanceBuilder()
        builder.set_objective(objective)
        builder.set_item_item_minimum_spacing(job["Gap"])
        bin_to_table = {}
        named_tables = job["ReservedTables"]
        stock = solver.build_rectangle(job["Length"], job["Width"])
        for table in range(min(bin_count, named_tables)):
            bin_id = builder.add_bin_type(stock, copies=1)
            bin_to_table[bin_id] = table
            for blocker in job["Obstacles"]:
                if blocker["Table"] != table:
                    continue
                defect = builder.add_defect(bin_id, -1, solver.ShapeWithHoles(
                    solver.build_shape(blocker["Points"]), []))
                builder.set_item_defect_minimum_spacing(bin_id, defect, job["Gap"])
        if bin_count > named_tables:
            bin_id = builder.add_bin_type(stock, copies=bin_count - named_tables)
            bin_to_table[bin_id] = None
        ids = {}
        for item in job["Items"]:
            rotations = [(a["Degrees"], a["Degrees"], a["Mirror"]) for a in item["Angles"]]
            ids[builder.add_item_type(solver.build_shape(item["Points"]),
                                      allowed_rotations=rotations, copies=1)] = item["Id"]

        def publish(solution):
            moves = []
            free_table = named_tables
            for i in range(solution.number_of_different_bins()):
                sheet = solution.bin(i)
                table = bin_to_table[sheet.bin_type_id]
                # The job models every requested copy as its own item type.
                if sheet.copies != 1:
                    continue
                if table is None:
                    table = free_table
                    free_table += 1
                for p in sheet.items:
                    moves.append(dict(Id=ids[p.item_type_id], Table=table, Degrees=p.angle,
                                      Mirror=p.mirror, X=p.bl_corner.x, Y=p.bl_corner.y))
            if moves:
                send(Placements=moves)

        config = solver.OptimizeParameters()
        config.time_limit = seconds
        config.verbosity_level = 0
        config.new_solution_callback = lambda result: publish(result.solution)
        result = solver.optimize(builder.build(), config)
        publish(result.solution)
        return result.solution.number_of_items()

    seconds = job["Seconds"]
    start = time.monotonic()
    maximum = job["MaxTables"]
    occupied_area = math.fsum(polygon_area(item["Points"])
                              for item in job["Items"] + job["Obstacles"])
    stock_area = job["Length"] * job["Width"]
    could_fit_one = occupied_area <= stock_area or math.isclose(occupied_area, stock_area)
    if job["ReservedTables"] <= 1 and (maximum == 1 or could_fit_one):
        single = solver.Objective.Knapsack if job["ReservedTables"] else solver.Objective.OpenDimensionX
        slice_seconds = seconds if maximum == 1 else seconds / 2
        if run(single, 1, slice_seconds) == len(job["Items"]):
            # Spend the remaining budget on the direct length objective; a
            # fixed table already has its original material footprint.
            remainder = seconds - (time.monotonic() - start)
            if maximum > 1 and not job["ReservedTables"] and remainder > 0:
                run(solver.Objective.OpenDimensionX, 1, remainder)
            return
    remainder = seconds - (time.monotonic() - start)
    if maximum > 1 and remainder > 0:
        run(solver.Objective.BinPackingWithLeftovers, maximum, remainder)
    elif not job["ReservedTables"] and remainder > 0:
        run(solver.Objective.Knapsack, 1, remainder)


def sparrow(job):
    from spyrrow import Item, ProgressQueue, StripPackingConfig, StripPackingInstance, ReportType

    items = [Item(str(item["Id"]), [tuple(point) for point in item["Points"]], 1,
                  [a["Degrees"] for a in item["Angles"]]) for item in job["Items"]]
    queue = ProgressQueue()
    finished = threading.Event()
    config = StripPackingConfig(total_computation_time=math.ceil(job["Seconds"]),
                                seed=job["Seed"], num_workers=job["Workers"],
                                min_items_separation=job["Gap"])
    # Sparrow also reserves the inter-part clearance at each strip edge.
    # Compensate for those two margins without changing part-to-part spacing.
    edge = job["Gap"]

    def publish(result):
        spans = []
        for p in result.placed_items:
            angle = math.radians(p.rotation)
            c, s = math.cos(angle), math.sin(angle)
            projected = [x * c - y * s for x, y in points[int(p.id)]]
            x0, y0 = p.translation
            spans.append((min(projected) + x0, max(projected) + x0, int(p.id), p.rotation, x0, y0))
        moves = []
        table = -1
        origin = 0
        for low, high, key, angle, x, y in sorted(spans):
            # A group preserves every solver-relative placement. Move the
            # whole group to its own stock piece when it would exceed length.
            if table < 0 or high - origin > job["Length"]:
                table += 1
                origin = low
            if table == job["MaxTables"]:
                break
            moves.append(dict(Id=key, Table=table, Degrees=angle, Mirror=False, X=x-origin, Y=y-edge))
        if moves:
            send(Placements=moves)

    def drain():
        for kind, result in queue.drain():
            if kind != ReportType.ExplInfeas:
                publish(result)

    def progress():
        while not finished.wait(PROGRESS_POLL_SECONDS):
            drain()

    points = {item["Id"]: item["Points"] for item in job["Items"]}
    watcher = threading.Thread(target=progress, daemon=True)
    watcher.start()
    try:
        result = StripPackingInstance("vNest", job["Width"] + 2 * edge, items).solve(config, queue)
    finally:
        finished.set()
        watcher.join()
    drain()
    publish(result)


def main():
    job = json.load(sys.stdin)
    {"PackingSolver": packing_solver, "Sparrow": sparrow}[job["Engine"]](job)
    send(Done=True)


if __name__ == "__main__":
    try:
        main()
    except BaseException as error:
        send(Error=str(error))
        sys.exit(1)
