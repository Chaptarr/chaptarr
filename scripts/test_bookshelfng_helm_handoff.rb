#!/usr/bin/env ruby
# frozen_string_literal: true

require "open3"
require "json"
require "tmpdir"
require "yaml"

ROOT = File.expand_path("..", __dir__)
CHART = File.join(ROOT, "charts", "bookshelfng")
DIGEST = "sha256:#{"0" * 64}"
BRIDGE_VERSION = "1.2.3"

def run_helm(*args)
  stdout, stderr, status = Open3.capture3("helm", *args)
  [stdout, stderr, status]
end

def render(*overrides)
  run_helm(
    "template", "bookshelfng", CHART,
    "--namespace", "bookshelf-test",
    *overrides
  )
end

def render_chart(chart, *overrides)
  Open3.capture3(
    "helm", "template", "bookshelfng", chart,
    "--namespace", "bookshelf-test",
    *overrides
  )
end

def render_legacy(chart, *overrides)
  stdout, stderr, status = Open3.capture3(
    "helm", "template", "bookshelfng", chart,
    "--namespace", "bookshelf-test",
    *overrides
  )
  raise "legacy chart render failed: #{stderr}" unless status.success?

  YAML.load_stream(stdout).compact
end

def normalize_expected_upgrade_differences(value)
  case value
  when Hash
    value.each do |key, nested|
      if key == "image"
        value[key] = "<application-image>"
      elsif ["app.kubernetes.io/version", "helm.sh/chart"].include?(key)
        value[key] = "<release-version>"
      else
        value[key] = normalize_expected_upgrade_differences(nested)
      end
    end
  when Array
    value.map! { |nested| normalize_expected_upgrade_differences(nested) }
  end

  value
end

def normalize_bookshelfng_handoff_fields(value)
  normalize_expected_upgrade_differences(value)
  return value unless value["kind"] == "StatefulSet"

  pod_spec = value.dig("spec", "template", "spec")
  return value unless pod_spec.is_a?(Hash)

  pod_spec.delete("terminationGracePeriodSeconds")
  container = pod_spec.fetch("containers", []).find { |item| item["name"] == "bookshelfng" }
  return value unless container

  %w[startupProbe readinessProbe livenessProbe].each { |probe| container.delete(probe) }
  compatibility_environment = %w[
    CHAPTARR_BOOKSHELF_COMPATIBILITY
    CHAPTARR_DEFAULT_PORT
    CHAPTARR_EXPECTED_PORT
    BOOKSHELFNG_MIGRATION_MODE
    HARDCOVER
    HARDCOVER_NATIVE
  ]
  container["env"]&.reject! { |entry| compatibility_environment.include?(entry["name"]) }
  container.delete("env") if container["env"]&.empty?
  value
end

def compare_rendered_resources(candidate_objects, legacy_objects, scenario)
  index = lambda do |objects|
    objects.each_with_object({}) do |item, resources|
      next unless item.is_a?(Hash) && item["kind"] && item.dig("metadata", "name")

      resources[[item["kind"], item.dig("metadata", "name")]] = item
    end
  end

  candidate = index.call(candidate_objects)
  legacy = index.call(legacy_objects)
  assert(candidate.keys.sort == legacy.keys.sort,
         "#{scenario}: rendered resource identities differ: candidate=#{candidate.keys.inspect} legacy=#{legacy.keys.inspect}")

  candidate.each do |identity, item|
    legacy_item = legacy.fetch(identity)
    candidate_resource = normalize_bookshelfng_handoff_fields(JSON.parse(JSON.generate(item)))
    legacy_resource = normalize_bookshelfng_handoff_fields(JSON.parse(JSON.generate(legacy_item)))
    assert(candidate_resource == legacy_resource,
           "#{scenario}: #{identity[0]}/#{identity[1]} differs from the pinned BookshelfNG chart")
  end
end

def assert(condition, message)
  raise message unless condition
end

valid = [
  "--set", "bookshelfMigration.confirmed=true",
  "--set", "bookshelfMigration.providerMode=hardcover",
  "--set-string", "image.digest=#{DIGEST}"
]
stdout, stderr, status = render(*valid)
assert(status.success?, "valid chart render failed: #{stderr}")

objects = YAML.load_stream(stdout).compact
resource = lambda do |kind, name|
  objects.find do |item|
    item.is_a?(Hash) && item["kind"] == kind && item.dig("metadata", "name") == name
  end || raise("missing #{kind}/#{name} in rendered chart")
end

stateful_set = resource.call("StatefulSet", "bookshelfng")
service = resource.call("Service", "bookshelfng")
claim = resource.call("PersistentVolumeClaim", "bookshelfng-config")

assert(stateful_set.dig("spec", "serviceName") == "bookshelfng", "StatefulSet service identity changed")
assert(stateful_set.dig("spec", "replicas") == 1, "StatefulSet replica count changed")
selector = stateful_set.dig("spec", "selector", "matchLabels")
expected_selector = {
  "app.kubernetes.io/name" => "bookshelfng",
  "app.kubernetes.io/instance" => "bookshelfng"
}
assert(selector == expected_selector, "StatefulSet selector changed: #{selector.inspect}")
assert(stateful_set.dig("spec", "template", "metadata", "labels").slice(*expected_selector.keys) == expected_selector,
       "pod labels no longer match the legacy selector")

container = stateful_set.dig("spec", "template", "spec", "containers").find { |item| item["name"] == "bookshelfng" }
assert(!container.nil?, "legacy container name was not preserved")
assert(container["image"] == "ghcr.io/snapetech-org/chaptarrng@#{DIGEST}", "image is not pinned to the requested digest")
assert(container.dig("ports", 0, "containerPort") == 8787, "container port changed")
compatibility_environment = container.fetch("env", []).to_h { |item| [item["name"], item["value"]] }
assert(compatibility_environment["CHAPTARR_BOOKSHELF_COMPATIBILITY"] == "true", "BookshelfNG compatibility startup is not enabled")
assert(compatibility_environment["BOOKSHELFNG_MIGRATION_MODE"] == "automatic", "default migration mode changed")
assert(compatibility_environment["HARDCOVER"] == "true" && compatibility_environment["HARDCOVER_NATIVE"] == "true",
       "selected BookshelfNG provider mode is not passed to the container")
assert(container.dig("startupProbe", "exec", "command") == ["/usr/local/bin/chaptarr-bookshelf-liveness"] &&
       container.dig("startupProbe", "failureThreshold") == 1470,
       "startup probe no longer waits through bounded migration")
assert(container.dig("readinessProbe", "exec", "command") == ["/usr/local/bin/chaptarr-healthcheck"],
       "readiness probe does not check the migrated application health")
assert(container.dig("livenessProbe", "exec", "command") == ["/usr/local/bin/chaptarr-bookshelf-liveness"],
       "liveness probe would restart the app during migration or recovery")
assert(stateful_set.dig("spec", "template", "spec", "terminationGracePeriodSeconds") == 30600,
       "termination grace period does not cover migration recovery")
assert(container.dig("volumeMounts", 0, "name") == "config", "config volume mount name changed")
assert(container.dig("volumeMounts", 0, "mountPath") == "/config", "config mount path changed")
assert(stateful_set.dig("spec", "template", "spec", "volumes", 0, "persistentVolumeClaim", "claimName") == "bookshelfng-config",
       "StatefulSet no longer mounts the legacy PVC")

assert(service.dig("spec", "selector") == expected_selector, "Service selector changed")
assert(service.dig("spec", "ports", 0, "port") == 8787, "Service port changed")
assert(claim.dig("spec", "resources", "requests", "storage") == "10Gi", "PVC request changed")

legacy_chart = ENV["BOOKSHELFNG_REFERENCE_CHART"] || File.expand_path("../bookshelfng/charts/bookshelfng", ROOT)
assert(File.directory?(legacy_chart),
       "pinned BookshelfNG reference chart is required; set BOOKSHELFNG_REFERENCE_CHART to its charts/bookshelfng directory")
legacy_objects = render_legacy(legacy_chart, *valid)
compare_rendered_resources(objects, legacy_objects, "default release values")

preserved_values = valid + [
  "--set", "serviceAccount.create=true",
  "--set", "serviceAccount.name=bookshelfng-service",
  "--set", "ingress.enabled=true",
  "--set", "ingress.hosts[0].host=books.example.com",
  "--set", "ingress.hosts[0].paths[0].path=/",
  "--set", "ingress.hosts[0].paths[0].pathType=Prefix",
  "--set", "service.type=NodePort",
  "--set", "podAnnotations.backup=keep",
  "--set", "podLabels.migration=preserve",
  "--set", "persistence.storageClass=media",
  "--set-string", "resources.requests.cpu=100m",
  "--set-string", "resources.limits.memory=1Gi",
  "--set", "nodeSelector.role=library",
  "--set", "tolerations[0].key=dedicated",
  "--set", "tolerations[0].operator=Equal",
  "--set", "tolerations[0].value=books",
  "--set", "tolerations[0].effect=NoSchedule",
  "--set", "extraEnv[0].name=Bookshelf__Authentication__ApiKey",
  "--set", "extraEnv[0].valueFrom.secretKeyRef.name=bookshelfng-secret",
  "--set", "extraEnv[0].valueFrom.secretKeyRef.key=apiKey"
]
preserved_stdout, preserved_stderr, preserved_status = render(*preserved_values)
assert(preserved_status.success?, "candidate chart failed to render retained installation values: #{preserved_stderr}")
preserved_objects = YAML.load_stream(preserved_stdout).compact
legacy_preserved_objects = render_legacy(legacy_chart, *preserved_values)
compare_rendered_resources(preserved_objects, legacy_preserved_objects, "existing installation values")

Dir.mktmpdir("bookshelfng-helm-package-") do |directory|
  _, package_error, package_status = Open3.capture3("helm", "package", CHART, "--destination", directory)
  assert(package_status.success?, "chart packaging failed: #{package_error}")
  assert(Dir.glob(File.join(directory, "bookshelfng-*.tgz")).length == 1, "chart package was not produced")
end

Dir.mktmpdir("bookshelfng-helm-bridge-build-") do |directory|
  build_output = File.join(directory, "output")
  build_script = File.join(ROOT, "packaging", "scripts", "build-bookshelfng-helm-bridge.sh")
  build_env = {
    "VERSION" => BRIDGE_VERSION,
    "IMAGE_DIGEST" => DIGEST,
    "OUTPUT_DIR" => build_output
  }
  build_stdout, build_stderr, build_status = Open3.capture3(build_env, "bash", build_script)
  assert(build_status.success?, "versioned chart bridge build failed: #{build_stdout}\n#{build_stderr}")

  package_path = File.join(build_output, "bookshelfng-#{BRIDGE_VERSION}.tgz")
  assert(File.file?(package_path), "versioned chart bridge package is missing")
  chart_metadata, chart_error, chart_status = run_helm("show", "chart", package_path)
  assert(chart_status.success?, "versioned chart metadata could not be read: #{chart_error}")
  assert(chart_metadata.include?("name: bookshelfng") && chart_metadata.include?("version: #{BRIDGE_VERSION}"),
         "versioned chart identity or version changed")

  chart_values, values_error, values_status = run_helm("show", "values", package_path)
  assert(values_status.success?, "versioned chart values could not be read: #{values_error}")
  assert(chart_values.include?("repository: ghcr.io/snapetech-org/chaptarrng"),
         "versioned chart does not use the standard ChaptarrNG repository")
  assert(chart_values.include?("digest: \"#{DIGEST}\""), "versioned chart image digest is not pinned")

  chart_readme, readme_error, readme_status = run_helm("show", "readme", package_path)
  assert(readme_status.success?, "versioned chart guide could not be read: #{readme_error}")
  assert(chart_readme.include?("oci://ghcr.io/snapetech/bookshelfng/bookshelfng"),
         "versioned chart guide lost the existing OCI chart identity")
  assert(chart_readme.include?("--reuse-values"), "versioned chart guide does not preserve existing release values")
  assert(chart_readme.include?("--version #{BRIDGE_VERSION}") && chart_readme.include?(DIGEST),
         "versioned chart guide has unresolved or incorrect release values")

  %w[hardcover softcover].each do |provider_mode|
    package_args = [
      "--set", "bookshelfMigration.confirmed=true",
      "--set", "bookshelfMigration.providerMode=#{provider_mode}",
      "--set-string", "image.tag=#{provider_mode}",
      "--set-string", "image.digest=#{DIGEST}"
    ]
    rendered, render_error, render_status = render_chart(package_path, *package_args)
    assert(render_status.success?, "packaged chart failed to render for #{provider_mode}: #{render_error}")
    assert(rendered.include?("image: \"ghcr.io/snapetech-org/chaptarrng@#{DIGEST}\""),
           "packaged chart did not pin the ChaptarrNG image for #{provider_mode}")
    expected_hardcover = provider_mode == "hardcover" ? "true" : "false"
    assert(rendered.include?("name: HARDCOVER\n              value: \"#{expected_hardcover}\""),
           "packaged chart did not preserve the #{provider_mode} source mode")
    assert(rendered.include?("claimName: bookshelfng-config"),
           "packaged chart did not preserve the legacy PVC for #{provider_mode}")
  end
end

release_distribution = File.read(File.join(ROOT, ".github", "workflows", "release-distribution.yml"))
release_workflow = File.read(File.join(ROOT, ".github", "workflows", "release.yml"))
docker_workflow = File.read(File.join(ROOT, ".github", "workflows", "docker-publish.yml"))
assert(release_distribution.include?("MIGRATION_BRIDGE_GATE,,}") &&
       release_distribution.include?("BOOKSHELFNG_RELEASE_READY,,}"),
       "package retry workflow no longer requires both legacy bridge readiness gates")
assert(release_distribution.match?(/publish-legacy-helm-bridge:[\s\S]*?vars\.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == 'true'[\s\S]*?vars\.BOOKSHELFNG_RELEASE_READY == 'true'/),
       "Helm publisher is not gated by both retirement readiness variables")
assert(release_workflow.include?("vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == 'true' && vars.CHAPTARRNG_RELEASE_READY == 'true' && vars.BOOKSHELFNG_RELEASE_READY == 'true'"),
       "release workflow no longer requires all three readiness gates for legacy publishers")
legacy_readiness_expression = "vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == 'true' && vars.CHAPTARRNG_RELEASE_READY == 'true' && vars.BOOKSHELFNG_RELEASE_READY == 'true'"
assert(release_workflow.include?("dispatch-legacy-ghcr:") &&
       release_workflow.include?("if: ${{ #{legacy_readiness_expression} }}") &&
       release_workflow.include?("publish-packages:") &&
       release_workflow.include?("legacy_bridge_enabled: ${{ #{legacy_readiness_expression} }}"),
       "legacy bridge publishers are not explicitly gated by all readiness variables")
assert(docker_workflow.include?("inputs.legacy_bridge_enabled && #{legacy_readiness_expression}"),
       "legacy Docker aliases are not gated by all readiness variables")

[
  ["missing operator confirmation", ["--set", "bookshelfMigration.providerMode=hardcover", "--set-string", "image.digest=#{DIGEST}"], /confirmed must be true/],
  ["missing provider mode for unknown predecessor", ["--set", "bookshelfMigration.confirmed=true", "--set-string", "image.tag=unknown", "--set-string", "image.digest=#{DIGEST}"], /providerMode must be explicit because/],
  ["missing image digest", ["--set", "bookshelfMigration.confirmed=true", "--set", "bookshelfMigration.providerMode=hardcover"], /image.digest must be set/],
  ["legacy repository retained by --reuse-values", ["--set", "bookshelfMigration.confirmed=true", "--set", "bookshelfMigration.providerMode=hardcover", "--set", "image.repository=ghcr.io/snapetech/bookshelfng", "--set-string", "image.digest=#{DIGEST}"], /image.repository must be the ChaptarrNG image/],
  ["malformed image digest", ["--set", "bookshelfMigration.confirmed=true", "--set", "bookshelfMigration.providerMode=hardcover", "--set-string", "image.digest=latest"], /image.digest must have the form/],
  ["multiple replicas", ["--set", "bookshelfMigration.confirmed=true", "--set", "bookshelfMigration.providerMode=hardcover", "--set-string", "image.digest=#{DIGEST}", "--set", "replicaCount=2"], /replicaCount must be 0 or 1/]
].each do |name, overrides, error|
  _, error_output, error_status = render(*overrides)
  assert(!error_status.success?, "#{name} unexpectedly rendered")
  assert(error_output.match?(error), "#{name} failed for an unexpected reason: #{error_output}")
end

custom_repository_args = valid + ["--set", "image.repository=registry.example/chaptarrng", "--set", "image.allowCustomRepository=true"]
custom_stdout, custom_stderr, custom_status = render(*custom_repository_args)
assert(custom_status.success?, "explicit image mirror render failed: #{custom_stderr}")
custom_objects = YAML.load_stream(custom_stdout).compact
custom_stateful_set = custom_objects.find { |item| item.is_a?(Hash) && item["kind"] == "StatefulSet" }
custom_container = custom_stateful_set.dig("spec", "template", "spec", "containers").find { |item| item["name"] == "bookshelfng" }
assert(custom_container["image"] == "registry.example/chaptarrng@#{DIGEST}", "explicit image mirror was not rendered")

helm_workflow = File.read(File.join(ROOT, ".github", "workflows", "helm-handoff.yml"))
installed_handoff = File.read(File.join(ROOT, "scripts", "test_bookshelfng_helm_install_handoff.sh"))
assert(helm_workflow.include?("test_bookshelfng_helm_install_handoff.sh") &&
       helm_workflow.include?("docker.io/snapetech/bookshelfng:hardcover-v0.4.21.76") &&
       helm_workflow.include?("BOOKSHELFNG_LEGACY_IMAGE_DIGEST: sha256:40a53ac528b6789ca78686b4303bb6c436cd191cd813f915ea171d72b47a8c38") &&
       helm_workflow.include?(%q[inputs.runner_pool == 'public' && 'ubuntu-24.04']) &&
       helm_workflow.include?(%q[BUILDKIT_EGRESS_PROXY: ${{ inputs.runner_pool == 'private' && github.ref == 'refs/heads/main' && 'http://10.0.2.100:3128' || '' }]) &&
       helm_workflow.include?(%q[RUNNER_POOL: ${{ inputs.runner_pool }]) &&
       installed_handoff.include?(%q[if [[ "$RUNNER_POOL" == "public" ]]; then]) &&
       installed_handoff.include?(%q[elif [[ "$RUNNER_POOL" == "private" && "$BUILDKIT_EGRESS_PROXY" == "http://10.0.2.100:3128" ]]; then]) &&
       helm_workflow.include?("Upload Helm installed-handoff evidence"),
       "Helm workflow no longer runs and retains the installed BookshelfNG .76 handoff")
[
  ["manual deferral", "upgrade_candidate manual 1"],
  ["automatic conversion", "upgrade_candidate automatic 1 --wait"],
  ["populated schema-158 fixture", "BookshelfNG.PopulatedMigration158.sql"],
  ["populated row and identity continuity", "assert_migrated_fixture"],
  ["same persistent claim through all revisions", "assert_pvc_identity"],
  ["recorded persistent claim provenance", "SOURCE_PVC_UID"],
  ["preserved SQLite source backup", "readarr.db.bookshelfng-backup-*.db"],
  ["confirmed chart restore", "bookshelfMigration.rollback.confirmed=true"],
  ["BookshelfNG recovery", "assert_ping bookshelfng-after-rollback"],
  ["BookshelfNG stopped before source snapshot verification", 'kubectl scale statefulset/bookshelfng -n "$NAMESPACE" --replicas=0 --timeout=5m'],
  ["logical source database restored", 'test "$RESTORED_SQL_DUMP_SHA256" = "$SOURCE_SQL_DUMP_SHA256"'],
  ["config restored", 'test "$RESTORED_CONFIG_SHA256" = "$CONFIG_SHA256"'],
  ["canary restored", 'test "$RESTORED_CANARY_SHA256" = "$CANARY_SHA256"']
].each do |label, contract|
  assert(installed_handoff.include?(contract), "installed Helm acceptance is missing #{label}")
end
assert(installed_handoff.scan('kubectl apply -f "$EVIDENCE_DIR/snapshot-pod.yaml"').count >= 2,
       "installed Helm acceptance must verify the source snapshot before handoff and after rollback")
assert(!installed_handoff.include?("--set persistence.existingClaim="),
       "Helm upgrade must keep managing the stable PVC resource instead of dropping it")

puts "BookshelfNG Helm contract passed: pinned legacy resource identities and values, bridge package, provider modes, readiness gates, and a real installed .76 No/Yes/backup/restore/recovery path are wired."
