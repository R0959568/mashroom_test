"""Database connection and notebook helpers for the Citi Bike notebooks.

The database is the project Postgres (citibike, on the project server via NetBird). It uses
pg8000, a pure-Python driver: no compiled libpq DLL, so it also works on laptops where
endpoint security or a firewall blocks psycopg.

Credentials come from the git-ignored .env in the repo root (see .env.example):

    CITIBIKE_DB_USER=...
    CITIBIKE_DB_PASSWORD=...

Usage in a notebook under citibike/notebooks/:

    import sys
    sys.path.insert(0, "..")          # citibike/ -> connection.py
    from connection import sql, fmt, check_connection, notebook_style, COLORS, LABELS, millions, thousands

    check_connection()
    notebook_style()                  # chart style + number format for every DataFrame shown

    df = sql("SELECT * FROM citibike.trips WHERE started_at >= :d LIMIT 10", d="2026-08-01")

The data products of 04 onwards live as tables in the schema citibike_prep, owned by the write role. Three
helpers cover that path (the logins start every transaction read-only, see 04 appendix):

    from connection import run, copy_into, copy_out, PREP_SCHEMA
    run("CREATE TABLE IF NOT EXISTS citibike_prep.t (...)")         # one statement, read-write, as the write role
    copy_into("t", df, ["col1", "col2"], delete_where="true")        # COPY a DataFrame into citibike_prep.t
    df = copy_out("SELECT * FROM citibike_prep.t")                   # millions of rows back, through COPY TO STDOUT
"""

import io
import os
import time

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from dotenv import load_dotenv
from matplotlib.ticker import FuncFormatter
from sqlalchemy import create_engine, text

load_dotenv()   # finds the .env in the repo root, also from citibike/notebooks/

engine = create_engine(
    "postgresql+pg8000://",
    connect_args={
        "host": "100.88.194.89",
        "port": 5432,
        "database": "citibike",
        "user": os.environ["CITIBIKE_DB_USER"],
        "password": os.environ["CITIBIKE_DB_PASSWORD"].strip(),
        # In pg8000 "timeout" is a socket timeout on every read, not only on connecting (unlike psycopg's
        # connect_timeout). 660 s is just above the server's statement_timeout of 10 minutes: a legitimate
        # query is always answered (or cancelled by the server) before it fires, while a dead connection
        # (VPN hiccup, connection closed while idle) fails after 11 minutes instead of hanging for hours.
        "timeout": 660,
    },
    # Check a pooled connection with a tiny round trip before reusing it, so a connection the server
    # closed while the kernel was idle is replaced instead of handed to the next query.
    pool_pre_ping=True,
)


def check_connection() -> None:
    """Print the database user, to confirm the connection and the credentials work."""
    with engine.connect() as conn:
        print("Connected as", conn.execute(text("SELECT current_user")).scalar())


def fmt(value, spec: str = ",") -> str:
    """Format a number with a dot as thousands separator and a comma as decimal separator: 212.310.903 / 0,057."""
    return f"{value:{spec}}".translate(str.maketrans(",.", ".,"))


def sql(query: str, **params) -> pd.DataFrame:
    """Run a SELECT and return the result as a DataFrame. Pass values as :name parameters."""
    t0 = time.time()
    df = pd.read_sql(text(query), engine, params=params)
    print(f"{fmt(len(df))} rows in {fmt(time.time() - t0, '.1f')} s")
    return df


PREP_SCHEMA = "citibike_prep"            # the data products of 04 onwards
WRITER_ROLE = "citibike_prep_writer"     # owns every table in that schema; the logins are members of it


def run(statement: str) -> int:
    """Run one DDL or DML statement in its own read-write transaction, as the write role. Returns the row count.

    Reads only Postgres tables: a statement that reads the view citibike.trips must go through sql() or
    copy_out() as the login (pg_duckdb refuses INSERT ... SELECT, and the write role cannot read schema citibike)."""
    t0 = time.time()
    with engine.begin() as conn:
        conn.execute(text("SET TRANSACTION READ WRITE"))        # the logins start read-only; this one writes
        conn.execute(text(f"SET LOCAL ROLE {WRITER_ROLE}"))     # objects end up owned by the role
        rows = conn.execute(text(statement)).rowcount
    print(f"{fmt(rows) if rows is not None and rows >= 0 else '-'} rows in {fmt(time.time() - t0, '.1f')} s")
    return rows


def copy_into(table: str, df: pd.DataFrame, columns: list[str], delete_where: str | None = None) -> None:
    """COPY a DataFrame into citibike_prep.<table> as the write role; delete_where first removes the same rows."""
    t0 = time.time()
    buf = io.BytesIO(df[columns].to_csv(index=False, header=False).encode())
    raw = engine.raw_connection()
    try:
        cur = raw.cursor()
        cur.execute("SET TRANSACTION READ WRITE")
        cur.execute(f"SET LOCAL ROLE {WRITER_ROLE}")
        if delete_where:
            cur.execute(f"DELETE FROM {PREP_SCHEMA}.{table} WHERE {delete_where}")
        cur.execute(f"COPY {PREP_SCHEMA}.{table} ({', '.join(columns)}) FROM STDIN WITH (FORMAT csv)", stream=buf)
        raw.commit()
    except Exception:
        raw.rollback()
        raise
    finally:
        raw.close()
    print(f"{fmt(len(df))} rows copied into {PREP_SCHEMA}.{table} ({fmt(len(buf.getvalue()) / 1e6, '.1f')} MB CSV) "
          f"in {fmt(time.time() - t0, '.1f')} s")


def copy_out(query: str, **read_csv_kwargs) -> pd.DataFrame:
    """Run a SELECT and return the rows as a DataFrame through COPY ... TO STDOUT (CSV).

    Much faster than sql() for millions of rows: the server writes CSV and pandas parses it, pg8000 (pure
    Python) touches no value. Booleans come back as t/f and are mapped; pass parse_dates=[...] for date columns
    and dtype={"station_key": "string"} for ids that look like numbers (7386.10 must not become 7386.1)."""
    t0 = time.time()
    buf = io.BytesIO()
    raw = engine.raw_connection()
    try:
        cur = raw.cursor()
        cur.execute(f"COPY ({query}) TO STDOUT WITH (FORMAT csv, HEADER)", stream=buf)
    finally:
        raw.close()
    size = len(buf.getvalue())
    buf.seek(0)
    df = pd.read_csv(buf, true_values=["t"], false_values=["f"], **read_csv_kwargs)
    print(f"{fmt(len(df))} rows ({fmt(size / 1e6, '.0f')} MB CSV) in {fmt(time.time() - t0, '.1f')} s")
    return df


COLORS = {"member": "#2a78d6", "casual": "#eb6834", "classic_bike": "#1baf7a", "electric_bike": "#4a3aa7"}
LABELS = {"member": "Member", "casual": "Casual", "classic_bike": "Classic bike", "electric_bike": "E-bike"}

# Axis formatters: ax.yaxis.set_major_formatter(millions)
millions = FuncFormatter(lambda v, _: f"{fmt(v / 1e6, '.1f')} M")
thousands = FuncFormatter(lambda v, _: f"{v / 1e3:.0f} k")

# Every DataFrame the notebook shows gets the same number format as fmt(). Integer columns get a thousands
# separator; float columns get as many decimals as their values need (at most pandas' display precision).
# Columns that hold a year are left alone, otherwise 2025 would show as 2.025.
NO_SEPARATOR = {"year"}


def _decimals(col: pd.Series) -> int:
    values = col.dropna().to_numpy()
    for d in range(pd.get_option("display.precision")):
        if np.allclose(values, values.round(d)):
            return d
    return pd.get_option("display.precision")


def _formatted_html(df: pd.DataFrame) -> str:
    formats = {}
    for name, col in df.items():
        if name in NO_SEPARATOR or pd.api.types.is_bool_dtype(col):
            continue
        if pd.api.types.is_integer_dtype(col):
            formats[name] = fmt
        elif pd.api.types.is_float_dtype(col):
            formats[name] = lambda v, spec=f",.{_decimals(col)}f": fmt(v, spec)
    return df.style.format(formats, na_rep="").to_html(table_attributes='class="dataframe"')


def notebook_style() -> None:
    """Apply the project chart style and show every DataFrame with the fmt() number format."""
    plt.rcParams.update({
        "figure.figsize": (11, 4.2), "figure.dpi": 110,
        "axes.spines.top": False, "axes.spines.right": False,
        "axes.grid": True, "grid.color": "#e6e6e3", "grid.linewidth": 0.8, "axes.axisbelow": True,
        "axes.edgecolor": "#8a8986", "axes.labelcolor": "#52514e", "xtick.color": "#52514e", "ytick.color": "#52514e",
        "axes.titlesize": 12, "axes.titleweight": "bold", "axes.titlelocation": "left",
        "lines.linewidth": 2, "legend.frameon": False,
    })
    pd.set_option("styler.render.max_rows", 60)   # a styled table does not truncate itself like a plain one

    from IPython import get_ipython
    ipython = get_ipython()
    if ipython is not None:   # only inside a notebook
        ipython.display_formatter.formatters["text/html"].for_type(pd.DataFrame, _formatted_html)
