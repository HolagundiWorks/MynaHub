"""Builds the Demo Villa drawing (ground floor plan, grid, columns, doors, windows, rooms, measured take-off marks and five structural details) into a project file."""
import json, subprocess, sys, os

# Usage: python build_demo_villa.py [aad-cli.exe] [project.osp] [plan.png]
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.expandvars(r'%LOCALAPPDATA%\Programs\O-Draft\aad-cli.exe')
OSP = sys.argv[2] if len(sys.argv) > 2 else r'D:\Work Development\DemoCompany\projects\tc-01-1\project.osp'
PNG = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(OSP), 'demo-plan.png')

c = []


def cmd(i, **p):
    c.append((i, p))


# --- the site: layers, take-off layers, one floor, a structural grid 3 bays x 2 rows
cmd('hcwlayers', set='standard')
cmd('takeoff.start')
cmd('floors', count=1)
cmd('openings.std')
cmd('grid.axis', origin=[0, 0], bays=[4000, 4000, 4000], rows=[4500, 4500])

# --- walls: 230 mm outer, 115 mm partitions (faces on A-WALL)
cmd('wall.draw', points=[[0, 0], [12000, 0], [12000, 9000], [0, 9000]], closed=True, thickness=230)
cmd('wall.draw', points=[[0, 4500], [12000, 4500]], thickness=115)
cmd('wall.draw', points=[[6000, 0], [6000, 4500]], thickness=115)
cmd('wall.draw', points=[[8000, 4500], [8000, 9000]], thickness=115)

# --- doors and windows, named after the schedule (D2 900 mm door, W3 window)
for at in ([6000, 0], [3000, 4500], [9000, 4500], [8000, 6750]):
    cmd('wall.door', at=at, name='D2')
for at in ([2000, 0], [10000, 0], [12000, 2250], [12000, 6750], [2000, 9000], [5500, 9000], [10000, 9000], [0, 2250]):
    cmd('wall.window', at=at, name='W3')

# --- rooms (label and rectangle on the room layers)
cmd('room', p1=[115, 115], p2=[5885, 4385], name='Living')
cmd('room', p1=[6115, 115], p2=[11885, 4385], name='Dining')
cmd('room', p1=[115, 4615], p2=[7885, 8885], name='Bedroom 1')
cmd('room', p1=[8115, 4615], p2=[11885, 8885], name='Bedroom 2')

# --- columns on the grid
cmd('column.place', size=[230, 450])

# --- measured take-off: the outer wall as four runs, partitions, columns, beams, slab, floor area
for pts in ([[0, 0], [12000, 0]], [[12000, 0], [12000, 9000]], [[12000, 9000], [0, 9000]], [[0, 9000], [0, 0]]):
    cmd('takeoff.draw', kind='extWall', thicknessMm=230, points=pts)
cmd('takeoff.draw', kind='intWall', thicknessMm=115, points=[[0, 4500], [12000, 4500]])
cmd('takeoff.draw', kind='intWall', thicknessMm=115, points=[[6000, 0], [6000, 4500]])
cmd('takeoff.draw', kind='intWall', thicknessMm=115, points=[[8000, 4500], [8000, 9000]])
for x in (0, 4000, 8000, 12000):
    for y in (0, 4500, 9000):
        cmd('takeoff.draw', kind='column', points=[[x - 115, y - 225], [x + 115, y - 225], [x + 115, y + 225], [x - 115, y + 225]], closed=True)
for pts in ([[0, 0], [12000, 0]], [[0, 4500], [12000, 4500]], [[0, 9000], [12000, 9000]], [[0, 0], [0, 9000]], [[4000, 0], [4000, 9000]], [[8000, 0], [8000, 9000]], [[12000, 0], [12000, 9000]]):
    cmd('takeoff.draw', kind='beam', points=pts)
cmd('takeoff.draw', kind='slab', points=[[0, 0], [12000, 0], [12000, 9000], [0, 9000]], closed=True)
cmd('takeoff.draw', kind='floorArea', points=[[115, 115], [11885, 115], [11885, 8885], [115, 8885]], closed=True)

# --- the structural details on the same sheet, to the right of the plan
cmd('smartblock.insert', id='rcc.column.rect', at=[15500, 8000], params={'txt': 60})
cmd('smartblock.insert', id='rcc.beam.section', at=[18500, 8000], params={'txt': 60})
cmd('smartblock.insert', id='rcc.footing.section', at=[16000, 3500], params={'txt': 60})
cmd('smartblock.insert', id='rcc.footing.plan', at=[21500, 3500], params={'txt': 60})
cmd('smartblock.insert', id='rcc.beam.elevation', at=[18000, -1200], params={'txt': 60})

# --- title
cmd('text', at=[0, -3600], text='DEMO VILLA  TC-01  GROUND FLOOR PLAN', height=300)
cmd('dimlinear', p1=[0, 0], p2=[12000, 0], at=[6000, -2200])
cmd('dimlinear', p1=[0, 0], p2=[0, 9000], at=[-2500, 4500])

args = [EXE, 'run', '--metric']
for i, p in c:
    args += ['--cmd', f'{i} {json.dumps(p)}']
args += ['--export', PNG, '--save', OSP]
r = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
out = (r.stdout or '') + (r.stderr or '')
bad = [l for l in out.splitlines() if '"error"' in l.lower() or l.lower().startswith('error') or 'failed' in l.lower() or 'refused' in l.lower()]
print('exit', r.returncode, 'lines', len(out.splitlines()))
print('\n'.join(bad[:15]))
print(out.splitlines()[-1][:200] if out else '')
