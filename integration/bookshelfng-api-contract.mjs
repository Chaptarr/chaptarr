import { isDeepStrictEqual } from "node:util";

const secretKey =
  /(?:password|secret|token|api.?key|authorization|credential|mam.?id|session.?id)/i;

export function sanitizeApiResponse(value, key = "") {
  if (secretKey.test(key)) {
    return value === null || value === undefined || value === ""
      ? ""
      : "<redacted-present>";
  }
  if (Array.isArray(value)) {
    return value.map((item) => {
      if (key !== "fields" || !item || typeof item !== "object") {
        return sanitizeApiResponse(item);
      }
      const fieldDescription = [item.name, item.label, item.key, item.id]
        .filter((part) => typeof part === "string")
        .join(" ");
      const redactValue =
        item.privacy === true ||
        item.type === "password" ||
        secretKey.test(fieldDescription);
      return Object.fromEntries(
        Object.keys(item)
          .sort()
          .map((fieldKey) => [
            fieldKey,
            fieldKey === "value" && redactValue
              ? item.value === null || item.value === undefined || item.value === ""
                ? ""
                : "<redacted-present>"
              : sanitizeApiResponse(item[fieldKey], fieldKey),
          ]),
      );
    });
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((childKey) => [childKey, sanitizeApiResponse(value[childKey], childKey)]),
    );
  }
  return value;
}

const providerPresentationKeys = new Set([
  "advanced",
  "helpLink",
  "helpText",
  "helpTextWarning",
  "hidden",
  "isFloat",
  "label",
  "order",
  "placeholder",
  "section",
  "type",
  "unit",
]);

export function normalizeProviderApiResponse(value) {
  if (Array.isArray(value)) {
    return value.map(normalizeProviderApiResponse);
  }
  if (!value || typeof value !== "object") {
    return value;
  }

  const normalized = {};
  for (const [key, child] of Object.entries(value)) {
    if (key === "infoLink") continue;
    if (key === "fields" && Array.isArray(child)) {
      normalized[key] = child.map((field) => {
        if (!field || typeof field !== "object") {
          return field;
        }

        const normalizedField = {};
        for (const [fieldKey, fieldValue] of Object.entries(field)) {
          if (providerPresentationKeys.has(fieldKey)) continue;
          if (fieldKey === "selectOptions" && Array.isArray(fieldValue)) {
            normalizedField[fieldKey] = fieldValue.map((option) => {
              if (!option || typeof option !== "object") return option;
              return Object.fromEntries(
                Object.entries(option).filter(([optionKey]) =>
                  optionKey === "value" || optionKey === "parentValue",
                ),
              );
            });
          } else {
            normalizedField[fieldKey] = fieldValue;
          }
        }
        return normalizedField;
      });
    } else {
      normalized[key] = normalizeProviderApiResponse(child);
    }
  }
  return normalized;
}

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

  if (typeof value.implementation === "string") {
    return `implementation:${value.implementation}`;
  }

  return null;
}

function isProviderSchemaArray(values) {
  return Array.isArray(values) &&
    values.length > 0 &&
    values.every((value) =>
      value &&
      typeof value === "object" &&
      !Array.isArray(value) &&
      !Object.hasOwn(value, "id") &&
      typeof value.implementation === "string",
    );
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
      const providerSchemaArray =
        isProviderSchemaArray(source) && isProviderSchemaArray(target);

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
            !providerSchemaArray &&
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
