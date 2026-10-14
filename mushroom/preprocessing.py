"""Shared preprocessing pipeline for mushroom classification notebooks."""

from sklearn.compose import ColumnTransformer
from sklearn.impute import MissingIndicator, SimpleImputer
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder, RobustScaler

MISSING_CATEGORY = "__missing__"


def build_preprocessor(numeric_cols, categorical_cols):
    """Return a fresh, unfitted transformer for numerical and categorical columns."""
    numeric_cols = list(numeric_cols)
    categorical_cols = list(categorical_cols)

    if not numeric_cols and not categorical_cols:
        raise ValueError("At least one numerical or categorical feature is required.")
    if len(set(numeric_cols)) != len(numeric_cols):
        raise ValueError("Numerical feature names must be unique.")
    if len(set(categorical_cols)) != len(categorical_cols):
        raise ValueError("Categorical feature names must be unique.")
    if set(numeric_cols).intersection(categorical_cols):
        raise ValueError("Numerical and categorical feature lists must not overlap.")

    transformers = []
    if numeric_cols:
        numeric_transformer = Pipeline(
            steps=[
                ("imputer", SimpleImputer(strategy="median", keep_empty_features=True)),
                ("scaler", RobustScaler()),
            ]
        )
        transformers.append(("numeric", numeric_transformer, numeric_cols))
        transformers.append(
            (
                "numeric_missing",
                MissingIndicator(features="all", error_on_new=False),
                numeric_cols,
            )
        )
    if categorical_cols:
        categorical_transformer = Pipeline(
            steps=[
                (
                    "imputer",
                    SimpleImputer(
                        strategy="constant",
                        fill_value=MISSING_CATEGORY,
                        keep_empty_features=True,
                    ),
                ),
                ("onehot", OneHotEncoder(handle_unknown="ignore", sparse_output=False)),
            ]
        )
        transformers.append(("categorical", categorical_transformer, categorical_cols))

    return ColumnTransformer(
        transformers=transformers,
        remainder="drop",
        sparse_threshold=0.0,
    )
