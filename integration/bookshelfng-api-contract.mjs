import { isDeepStrictEqual } from "node:util";

function identityKey(value, path) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;

  if (path.endsWith("/fields") && typeof value.name === "string") {
    return `name:${value.name}`;
  }

  if (path.endsWith("/selectOptions") && Object.hasOwn(value, "value")) {
    return `value:${JSON.stringify(value.value)}`;
  }

  if (Object.hasOwn(value, "id")) {
    return `id:${String(value.id)}`;
  }

  return null;
}

function debugValue(value, depth = 0) {
  if (typeof value === "string") {
    return value.length > 160 ? value.slice(0, 160) + "…" : value;
  }
  if (value === null || typeof value !== "object") return value;
  if (depth >= 6) return { type: Array.isArray(value) ? "array" : "object" };

  if (Array.isArray(value)) {
    const items = value.slice(0, 40).map((item) => debugValue(item, depth + 1));
    if (value.length > items.length) items.push({ truncatedItems: value.length - items.length });
    return items;
  }

  const keys = Object.keys(value).sort();
  const result = Object.fromEntries(
    keys.slice(0, 60).map((key) => [key, debugValue(value[key], depth + 1)]),
  );
  if (keys.length > 60) result.truncatedKeys = keys.length - 60;
  return result;
}

export function compareResponses(expected, actual, { includeDebugValues = false } = {}) {
  const mismatchDetails = [];
  const additiveDetails = [];

  function record(destination, detail, expectedValue, actualValue) {
    if (destination.length >= 100) return;
    if (includeDebugValues) {
      if (expectedValue !== undefined) detail.sourceValue = debugValue(expectedValue);
      if (actualValue !== undefined) detail.targetValue = debugValue(actualValue);
    }
    destination.push(detail);
  }

  function compare(source, target, path = "") {
    if (mismatchDetails.length >= 100 || isDeepStrictEqual(source, target)) return;

    if (Array.isArray(source) && Array.isArray(target)) {
      const sourceIdentities = source.map((item) => identityKey(item, path));
      const targetIdentities = target.map((item) => identityKey(item, path));
      const canMatchByIdentity =
        source.length > 0 &&
        sourceIdentities.every((key) => key !== null) &&
        targetIdentities.every((key) => key !== null) &&
        new Set(sourceIdentities).size === sourceIdentities.length &&
        new Set(targetIdentities).size === targetIdentities.length;

      if (canMatchByIdentity) {
        const targetByIdentity = new Map(
          targetIdentities.map((key, index) => [key, { value: target[index], index }]),
        );
        const sourceKeys = new Set(sourceIdentities);
        const targetKeys = new Set(targetIdentities);
        const commonSourceOrder = sourceIdentities.filter((key) => targetKeys.has(key));
        const commonTargetOrder = targetIdentities.filter((key) => sourceKeys.has(key));

        if (!path.endsWith("/fields") &&
            !path.endsWith("/selectOptions") &&
            !isDeepStrictEqual(commonSourceOrder, commonTargetOrder)) {
          record(mismatchDetails, { path: path + "/order", kind: "array-order" });
        }

        source.forEach((item, index) => {
          const key = sourceIdentities[index];
          const match = targetByIdentity.get(key);
          if (!match) {
            record(mismatchDetails, {
              path: path + "/" + key,
              kind: "source-only-item",
            }, item);
            return;
          }
          compare(item, match.value, path + "/" + match.index);
        });

        target.forEach((item, index) => {
          const key = targetIdentities[index];
          if (!sourceKeys.has(key)) {
            record(additiveDetails, {
              path: path + "/" + index,
              kind: "target-only-item",
            }, undefined, item);
          }
        });
        return;
      }

      if (source.length > target.length) {
        record(mismatchDetails, {
          path: path + "/length",
          kind: "array-length",
          sourceLength: source.length,
          targetLength: target.length,
        });
      } else if (target.length > source.length) {
        record(additiveDetails, {
          path: path + "/length",
          kind: "target-array-extension",
          sourceLength: source.length,
          targetLength: target.length,
        });
      }

      for (let index = 0; index < Math.min(source.length, target.length); index++) {
        compare(source[index], target[index], path + "/" + index);
        if (mismatchDetails.length >= 100) break;
      }
      return;
    }

    if (
      source &&
      target &&
      typeof source === "object" &&
      typeof target === "object" &&
      !Array.isArray(source) &&
      !Array.isArray(target)
    ) {
      const keys = new Set([...Object.keys(source), ...Object.keys(target)]);
      for (const key of [...keys].sort()) {
        if (!(key in source)) {
          record(additiveDetails, { path: path + "/" + key, kind: "target-only" }, undefined, target[key]);
        } else if (!(key in target)) {
          record(mismatchDetails, { path: path + "/" + key, kind: "source-only" }, source[key]);
        } else {
          compare(source[key], target[key], path + "/" + key);
        }
        if (mismatchDetails.length >= 100) break;
      }
      return;
    }

    record(mismatchDetails, {
      path: path || "/",
      kind:
        typeof source !== typeof target ||
        Array.isArray(source) !== Array.isArray(target)
          ? "type-difference"
          : "value-difference",
    }, source, target);
  }

  compare(expected, actual);
  return {
    mismatchPaths: mismatchDetails.map((detail) => detail.path),
    mismatchDetails,
    additivePaths: additiveDetails.map((detail) => detail.path),
    additiveDetails,
  };
}
