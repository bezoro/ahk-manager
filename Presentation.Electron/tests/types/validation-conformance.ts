import {
  array,
  bool,
  integer,
  nullable,
  oneOf,
  requireValid,
  shape,
  shapeFor,
  text,
  unknownValue,
  type Validator,
} from '../../src/shared/validation.js';

interface Dto {
  id: string;
  count: number;
  active?: boolean | null;
}

/** Compile-only regressions: tsc must reject each marked schema/DTO disagreement. */
export function checkValidationConformance(value: unknown): void {
  const dto = shapeFor<Dto>()({ id: text(), count: integer }, { active: nullable(bool) });
  const validated: Dto = requireValid(value, dto, 'DTO');
  validated.id.toUpperCase();
  validated.count.toFixed();

  const inferred = requireValid(
    value,
    shape({ id: text(), items: array(integer), mode: oneOf('one', 'two') }, { active: bool }),
    'inferred object',
  );
  inferred.id.toUpperCase();
  inferred.items.map((item) => item.toFixed());
  const mode: 'one' | 'two' = inferred.mode;
  const active: boolean | undefined = inferred.active;
  void mode;
  void active;

  // @ts-expect-error A DTO's required count field cannot disappear from its schema.
  shapeFor<Dto>()({ id: text() }, { active: nullable(bool) });

  shapeFor<Dto>()(
    {
      id: text(),
      // @ts-expect-error A number field cannot use a string validator.
      count: text(),
    },
    { active: nullable(bool) },
  );

  // @ts-expect-error Optional DTO fields require their own complete rule map.
  shapeFor<Dto>()({ id: text(), count: integer });

  // @ts-expect-error Declared optional fields cannot be omitted from the rule map.
  shapeFor<Dto>()({ id: text(), count: integer }, {});

  shapeFor<Dto>()(
    { id: text(), count: integer },
    {
      // @ts-expect-error The optional active field must validate a boolean or null.
      active: text(),
    },
  );

  const extraRequired = { id: text(), count: integer, removed: bool };
  // @ts-expect-error Extra fields are rejected even when rules come from a variable.
  shapeFor<Dto>()(extraRequired, { active: nullable(bool) });

  const extraOptional = { active: nullable(bool), removed: text() };
  // @ts-expect-error Removed optional DTO fields cannot survive in the schema.
  shapeFor<Dto>()({ id: text(), count: integer }, extraOptional);

  // @ts-expect-error An entirely optional DTO cannot require one of its fields.
  shapeFor<{ id?: string }>()({ id: text() }, { id: text() });

  // @ts-expect-error A DTO without optional fields cannot declare an optional rule.
  shapeFor<{ id: string }>()({ id: text() }, { removed: text() });

  // @ts-expect-error A numeric schema cannot claim it returned a string field.
  requireValid<{ count: string }>(value, shape({ count: integer }), 'wrong target');

  const untyped: Validator = () => true;
  // @ts-expect-error A boolean check alone cannot authenticate a DTO's result type.
  requireValid<Dto>(value, untyped, 'untyped check');

  // @ts-expect-error An unchecked custom field remains unknown, not a string.
  requireValid<{ id: string }>(value, shape({ id: untyped }), 'unknown field');

  // @ts-expect-error Intentional unknown pass-through cannot authenticate a DTO.
  requireValid<Dto>(value, unknownValue, 'unknown payload');
}
