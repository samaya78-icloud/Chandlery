"""
One-off conversion of a Chandlery database from integer keys to GUID (UUIDv7) keys.

How it works (nothing in the old data is altered):
  1. The old tables must already sit in schema "legacy" (ALTER SCHEMA public RENAME TO legacy; CREATE SCHEMA public;).
  2. The new schema was created in "public" by the InitialGuidSchema migration (dotnet ef database update).
  3. This script builds an id map (old integer id -> new UUIDv7) for every table with a GUID key, copies every row
     across with each foreign key remapped, resets identity counters, then verifies row counts and foreign keys.

Usage: python convert.py <database name>
The generated SQL is written next to this file as convert.generated.sql for review.
"""
import os
import subprocess
import sys

DB = sys.argv[1]
PSQL = r'C:\Program Files\PostgreSQL\17\bin\psql.exe'
ENV = dict(os.environ, PGPASSWORD=os.environ.get('PGPASSWORD', 'postgres'))
HERE = os.path.dirname(os.path.abspath(__file__))


def psql(sql=None, file=None, fields=False):
    cmd = [PSQL, '-h', 'localhost', '-U', 'postgres', '-d', DB, '-X', '-t', '-A', '-F', '|', '-v', 'ON_ERROR_STOP=1']
    cmd += ['-f', file] if file else ['-c', sql]
    r = subprocess.run(cmd, capture_output=True, text=True, env=ENV)
    if r.returncode != 0:
        print(r.stdout)
        print(r.stderr)
        sys.exit('psql failed')
    rows = [line.split('|') for line in r.stdout.splitlines() if line.strip()]
    return rows


# Which table a foreign-key-like column points at (by column name). Anything uuid that is not listed here stops the run.
REF = {
    'CompanyId': 'Companies', 'DepartmentId': 'Departments', 'VesselId': 'Vessels', 'ItemId': 'Items', 'SupplierId': 'Suppliers',
    'WinningSupplierId': 'Suppliers', 'CategoryId': 'Categories', 'ParentCategoryId': 'Categories',
    'PurchaseOrderId': 'PurchaseOrders', 'InvoiceId': 'Invoices', 'RfqId': 'Rfqs', 'RequisitionId': 'Requisitions',
    'RequisitionLineId': 'RequisitionLine', 'RfqLineId': 'RfqLine', 'TaxCodeId': 'TaxCodes', 'BankAccountId': 'SupplierBankAccounts',
    'DeliveryPortId': 'Ports', 'OrderPortId': 'Ports', 'FromUomId': 'Uoms', 'ToUomId': 'Uoms', 'QuoteId': 'Quotes',
    'DeliveryOrderId': 'DeliveryOrders', 'ApproverUserId': 'Users', 'DecidedByUserId': 'Users', 'UserId': 'Users',
    'RecordedByUserId': 'Users', 'CreatedByUserId': 'Users',
}
AUDIT_TARGETS = {'Requisition': 'Requisitions', 'Rfq': 'Rfqs', 'PurchaseOrder': 'PurchaseOrders', 'Invoice': 'Invoices'}
SKIP_TABLES = {'__EFMigrationsHistory', 'NodeInfos', 'NumberSequences', 'zz_idmap'}


def columns(schema):
    rows = psql(f"""select table_name, column_name, data_type, udt_name, is_nullable, coalesce(column_default,''), is_identity
                    from information_schema.columns where table_schema='{schema}' order by table_name, ordinal_position""")
    out = {}
    for t, c, dt, udt, nullable, default, ident in rows:
        out.setdefault(t, []).append(dict(name=c, type=dt, udt=udt, nullable=nullable == 'YES', default=default, identity=ident == 'YES'))
    return out


new = columns('public')
old = columns('legacy')
old_names = {t: {c['name'] for c in cols} for t, cols in old.items()}

id_tables = [t for t, cols in new.items() if t not in SKIP_TABLES and any(c['name'] == 'Id' and c['type'] == 'uuid' for c in cols)]
print('tables with a GUID key:', len(id_tables))

sql = []
sql.append('SET session_replication_role = replica;')
# A UUIDv7 for a given millisecond timestamp. Legacy rows get one millisecond apart, in their old id order and a little before
# "now", so newest-first ordering by key still works and records created after the conversion sort after all converted ones.
sql.append("""CREATE FUNCTION pg_temp.uuid7_at(ms bigint) RETURNS uuid AS $$
  SELECT encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) placing substring(int8send(ms) from 3) from 1 for 6), 52, 1), 53, 1), 'hex')::uuid
$$ LANGUAGE sql VOLATILE;""")
sql.append('CREATE UNLOGGED TABLE public.zz_idmap (tbl text NOT NULL, old_id integer NOT NULL, new_id uuid NOT NULL, PRIMARY KEY (tbl, old_id));')
for t in sorted(id_tables):
    if t not in old:
        continue
    sql.append(f"""INSERT INTO public.zz_idmap
SELECT '{t}', "Id", pg_temp.uuid7_at(floor(extract(epoch FROM now()) * 1000)::bigint - 100000000 + row_number() OVER (ORDER BY "Id"))
FROM legacy."{t}";""")

for t in sorted(new):
    if t in SKIP_TABLES:
        continue
    if t not in old:
        print('WARNING: no legacy table for', t)
        continue
    cols, exprs, joins = [], [], []
    has_identity = False
    for i, c in enumerate(new[t]):
        n = c['name']
        if n == 'OriginNodeId':
            continue  # new sync column; the legacy rows have no origin
        if n == 'Id' and c['type'] == 'uuid':
            joins.append(f"""JOIN public.zz_idmap m ON m.tbl = '{t}' AND m.old_id = l."Id\"""")
            exprs.append('m.new_id')
        elif t == 'AuditEntries' and n == 'EntityId':
            cases = ' '.join(
                f"""WHEN '{k}' THEN (SELECT new_id FROM public.zz_idmap WHERE tbl = '{v}' AND old_id = l."EntityId")""" for k, v in AUDIT_TARGETS.items())
            exprs.append(f"""COALESCE(CASE l."EntityType" {cases} END, '00000000-0000-0000-0000-000000000000'::uuid)""")
        elif c['type'] == 'uuid':
            ref = REF.get(n)
            if ref is None:
                sys.exit(f'No mapping for uuid column {t}.{n}')
            joins.append(f"""LEFT JOIN public.zz_idmap j{i} ON j{i}.tbl = '{ref}' AND j{i}.old_id = l."{n}\"""")
            exprs.append(f'j{i}.new_id')
        elif c['udt'] == '_uuid':
            exprs.append(f"""ARRAY(SELECT mm.new_id FROM unnest(l."{n}") WITH ORDINALITY u(old_id, ord) JOIN public.zz_idmap mm ON mm.tbl = 'Suppliers' AND mm.old_id = u.old_id ORDER BY u.ord)""")
        elif n in old_names[t]:
            exprs.append(f'l."{n}"')
        else:
            if not c['nullable'] and not c['default'] and not c['identity']:
                sys.exit(f'New required column without a default: {t}.{n}')
            continue  # the database default fills it (sync columns, short codes...)
        cols.append(f'"{n}"')
        has_identity = has_identity or c['identity']
    override = ' OVERRIDING SYSTEM VALUE' if has_identity else ''
    sql.append(f'INSERT INTO public."{t}" ({", ".join(cols)}){override}\nSELECT {", ".join(exprs)}\nFROM legacy."{t}" l\n' + '\n'.join(joins) + ';')

# identity counters of tables that keep integer identity keys (the owned line tables)
for t, cols in sorted(new.items()):
    for c in cols:
        if c['identity']:
            sql.append(f"""SELECT setval(pg_get_serial_sequence('public."{t}"', '{c['name']}'), COALESCE((SELECT max("{c['name']}") FROM public."{t}"), 1));""")

sql.append('DROP TABLE public.zz_idmap;' if '--keep-map' not in sys.argv else '-- map kept')
path = os.path.join(HERE, 'convert.generated.sql')
open(path, 'w', encoding='utf-8').write('\n\n'.join(sql))
print('running', path)
psql(file=path)

# ---- verification ----
bad = False
for t in sorted(new):
    if t in SKIP_TABLES or t not in old:
        continue
    a = int(psql(f'select count(*) from legacy."{t}"')[0][0])
    b = int(psql(f'select count(*) from public."{t}"')[0][0])
    flag = '' if a == b else '   <-- MISMATCH'
    if a != b:
        bad = True
    print(f'{t:24} legacy {a:5}  new {b:5}{flag}')

fks = psql("""select tc.table_name, kcu.column_name, ccu.table_name, ccu.column_name
              from information_schema.table_constraints tc
              join information_schema.key_column_usage kcu on kcu.constraint_name = tc.constraint_name and kcu.table_schema = tc.table_schema
              join information_schema.constraint_column_usage ccu on ccu.constraint_name = tc.constraint_name and ccu.table_schema = tc.table_schema
              where tc.constraint_type = 'FOREIGN KEY' and tc.table_schema = 'public'""")
for child, ccol, parent, pcol in fks:
    n = int(psql(f'select count(*) from public."{child}" c where c."{ccol}" is not null and not exists (select 1 from public."{parent}" p where p."{pcol}" = c."{ccol}")')[0][0])
    if n:
        bad = True
        print(f'ORPHANS: {child}.{ccol} -> {parent}.{pcol}: {n}')
print('foreign keys checked:', len(fks))
print('RESULT:', 'PROBLEMS FOUND' if bad else 'OK')
